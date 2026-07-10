using System.Collections.Generic;

namespace DfoServer.Game.Party
{
    /// <summary>组队操作的结果, 供 handler 决定向谁下发什么封包(格式无关)。</summary>
    public sealed class PartyOpResult
    {
        public bool Ok { get; set; }
        public string Reason { get; set; } = string.Empty;
        public Party Party { get; set; }

        /// <summary>受影响的目标 UserId(加入/离开/被踢者)。</summary>
        public ushort TargetUserId { get; set; }

        /// <summary>队伍是否已解散(成员清空后移除)。</summary>
        public bool Disbanded { get; set; }

        /// <summary>队长是否变更(队长离队时转移)。</summary>
        public bool LeaderChanged { get; set; }
        public ushort NewLeaderUserId { get; set; }

        /// <summary>操作后仍在队且需被通知的成员快照(离开/踢人时不含目标本人)。</summary>
        public List<PartyMember> RemainingMembers { get; set; } = new List<PartyMember>();

        /// <summary>建队/入队时若目标玩家原本在别的队伍, 这里带出原队的离队结果——
        /// 原队剩余成员需要收到离队通知, 不能被静默吞掉。null 表示原本无队。</summary>
        public PartyOpResult PriorPartyLeave { get; set; }

        public static PartyOpResult Fail(string reason) => new PartyOpResult { Ok = false, Reason = reason };
    }

    /// <summary>
    /// 组队生命周期与注册表(线程安全, 格式无关)。不负责封包收发 —— handler 查询本管理器后自行构建/下发。
    /// 队伍按分配的 PartyId 索引; 成员按 UserId(=CharacterId 截断)索引到所属队伍。
    /// </summary>
    public sealed class PartyManager
    {
        private readonly object _lock = new object();
        private readonly Dictionary<int, Party> _parties = new Dictionary<int, Party>();
        private readonly Dictionary<ushort, int> _userToParty = new Dictionary<ushort, int>();
        // 待应答的组队邀请: 被邀请者 UserId -> (邀请者 UserId, 目标队伍 PartyId)。
        // A 邀请 B 时登记, B 回 MEMBER_ENTER_REPLY 时消费。同一人重复被邀以最后一次为准。
        private readonly Dictionary<ushort, (ushort inviter, int partyId)> _pendingInvites
            = new Dictionary<ushort, (ushort inviter, int partyId)>();
        private int _nextPartyId = 1;

        /// <summary>查询某玩家所属队伍; 不在任何队伍返回 null。</summary>
        public Party GetPartyByUser(ushort userId)
        {
            lock (_lock)
            {
                if (_userToParty.TryGetValue(userId, out var pid) && _parties.TryGetValue(pid, out var party))
                    return party;
                return null;
            }
        }

        public Party GetPartyById(int partyId)
        {
            lock (_lock)
            {
                return _parties.TryGetValue(partyId, out var party) ? party : null;
            }
        }

        /// <summary>
        /// 创建一支新队伍, leader 成为队长与第一名成员。
        /// 若 leader 已在别的队伍, 先将其从原队移除, 原队的离队结果通过 PriorPartyLeave 带出供通知。
        /// </summary>
        public PartyOpResult CreateParty(PartyMember leader, bool singlePlay = false)
        {
            lock (_lock)
            {
                var prior = LeaveLocked(leader.UserId);

                var party = new Party(_nextPartyId++) { IsSinglePlay = singlePlay };
                party.TryAddMember(leader);
                party.LeaderUserId = leader.UserId;
                _parties[party.PartyId] = party;
                _userToParty[leader.UserId] = party.PartyId;
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = leader.UserId,
                    RemainingMembers = party.MembersBySlot(),
                    PriorPartyLeave = prior != null && prior.Ok ? prior : null,
                };
            }
        }

        /// <summary>把一名成员加入指定队伍。若其已在别的队伍先移除(原队结果经 PriorPartyLeave 带出)。满员/队伍不存在则失败。</summary>
        public PartyOpResult Join(int partyId, PartyMember member)
        {
            lock (_lock)
            {
                if (!_parties.TryGetValue(partyId, out var party))
                    return PartyOpResult.Fail("party_not_found");
                if (party.Contains(member.UserId))
                    return PartyOpResult.Fail("already_member");
                if (party.IsFull)
                    return PartyOpResult.Fail("party_full");

                var prior = LeaveLocked(member.UserId);

                if (!party.TryAddMember(member))
                    return PartyOpResult.Fail("add_failed");
                _userToParty[member.UserId] = party.PartyId;

                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = member.UserId,
                    RemainingMembers = party.MembersBySlot(),
                    PriorPartyLeave = prior != null && prior.Ok ? prior : null,
                };
            }
        }

        /// <summary>
        /// 某玩家离队。若离队后队伍为空则解散; 若离队者是队长则把队长转移给下一名成员。
        /// </summary>
        public PartyOpResult Leave(ushort userId)
        {
            lock (_lock)
            {
                return LeaveLocked(userId) ?? PartyOpResult.Fail("not_in_party");
            }
        }

        // 已持锁的离队实现。不在任何队伍返回 null。
        // 建队/入队前的自动清理与显式 Leave 共用这一份, 保证队长转移/换槽/解散逻辑只有一处。
        private PartyOpResult LeaveLocked(ushort userId)
        {
            if (!_userToParty.TryGetValue(userId, out var pid) || !_parties.TryGetValue(pid, out var party))
                return null;

            var wasLeader = party.LeaderUserId == userId;
            party.RemoveMember(userId);
            _userToParty.Remove(userId);

            var result = new PartyOpResult { Ok = true, Party = party, TargetUserId = userId };

            if (party.IsEmpty)
            {
                _parties.Remove(party.PartyId);
                result.Disbanded = true;
                return result;
            }

            if (wasLeader)
            {
                var next = party.MembersBySlot()[0];
                party.LeaderUserId = next.UserId;
                party.MoveToSlotZero(next.UserId);   // 客户端以 slot0=队长判定, 新队长必须排到 slot0
                result.LeaderChanged = true;
                result.NewLeaderUserId = next.UserId;
            }

            result.RemainingMembers = party.MembersBySlot();
            return result;
        }

        /// <summary>队长踢人。仅队长可踢, 且不能踢自己(踢自己走 Leave)。</summary>
        public PartyOpResult Kick(ushort byUserId, ushort targetUserId)
        {
            lock (_lock)
            {
                if (!_userToParty.TryGetValue(byUserId, out var pid) || !_parties.TryGetValue(pid, out var party))
                    return PartyOpResult.Fail("not_in_party");
                if (party.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");
                if (byUserId == targetUserId)
                    return PartyOpResult.Fail("cannot_kick_self");
                if (!party.Contains(targetUserId))
                    return PartyOpResult.Fail("target_not_member");

                party.RemoveMember(targetUserId);
                _userToParty.Remove(targetUserId);

                var result = new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    TargetUserId = targetUserId,
                    RemainingMembers = party.MembersBySlot(),
                };

                if (party.IsEmpty)
                {
                    _parties.Remove(party.PartyId);
                    result.Disbanded = true;
                }
                return result;
            }
        }

        /// <summary>队长手动转移。newLeader 必须是本队成员。</summary>
        public PartyOpResult TransferLeader(ushort byUserId, ushort newLeaderUserId)
        {
            lock (_lock)
            {
                if (!_userToParty.TryGetValue(byUserId, out var pid) || !_parties.TryGetValue(pid, out var party))
                    return PartyOpResult.Fail("not_in_party");
                if (party.LeaderUserId != byUserId)
                    return PartyOpResult.Fail("not_leader");
                if (!party.Contains(newLeaderUserId))
                    return PartyOpResult.Fail("target_not_member");

                party.LeaderUserId = newLeaderUserId;
                party.MoveToSlotZero(newLeaderUserId);   // 客户端以 slot0=队长判定, 需把新队长排到 slot0
                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    LeaderChanged = true,
                    NewLeaderUserId = newLeaderUserId,
                    RemainingMembers = party.MembersBySlot(),
                };
            }
        }

        /// <summary>解散整支队伍(清空索引)。返回解散前的成员快照供通知。</summary>
        public PartyOpResult Disband(int partyId)
        {
            lock (_lock)
            {
                if (!_parties.TryGetValue(partyId, out var party))
                    return PartyOpResult.Fail("party_not_found");

                var members = party.MembersBySlot();
                foreach (var m in members)
                    _userToParty.Remove(m.UserId);
                _parties.Remove(partyId);

                return new PartyOpResult
                {
                    Ok = true,
                    Party = party,
                    Disbanded = true,
                    RemainingMembers = members,
                };
            }
        }

        /// <summary>断线清理: 等价于 Leave, 供会话断开时调用。顺带清掉与该玩家相关的待应答邀请。</summary>
        public PartyOpResult OnSessionDisconnected(ushort userId)
        {
            lock (_lock)
            {
                _pendingInvites.Remove(userId);   // 作为被邀请者的待应答
                var stale = new List<ushort>();
                foreach (var kv in _pendingInvites)
                    if (kv.Value.inviter == userId) stale.Add(kv.Key);   // 作为邀请者发出的邀请
                foreach (var k in stale) _pendingInvites.Remove(k);

                return LeaveLocked(userId) ?? PartyOpResult.Fail("not_in_party");
            }
        }

        /// <summary>登记一条待应答邀请(A 邀请 B 入 A 的队)。同一被邀请者以最后一次覆盖。</summary>
        public void RecordInvite(ushort inviteeUserId, ushort inviterUserId, int partyId)
        {
            lock (_lock)
            {
                _pendingInvites[inviteeUserId] = (inviterUserId, partyId);
            }
        }

        /// <summary>消费一条待应答邀请(B 回应答时)。存在则返回 true 并移除, 输出邀请者与目标队伍。</summary>
        public bool TryConsumeInvite(ushort inviteeUserId, out ushort inviterUserId, out int partyId)
        {
            lock (_lock)
            {
                if (_pendingInvites.TryGetValue(inviteeUserId, out var v))
                {
                    _pendingInvites.Remove(inviteeUserId);
                    inviterUserId = v.inviter;
                    partyId = v.partyId;
                    return true;
                }
                inviterUserId = 0;
                partyId = 0;
                return false;
            }
        }

        public int PartyCount
        {
            get { lock (_lock) { return _parties.Count; } }
        }
    }
}
