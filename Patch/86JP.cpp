#include "86JP.h"
#include "HookInterface.h"
#include "XLog.h"

#include <intrin.h>
#include <mutex>

#pragma comment(lib, "user32.lib")

static uintptr_t dnf_base = 0;
static void* g_originalInventoryNoticeWrapper = nullptr;
static void* g_originalDirectInventoryNoticeWrapper = nullptr;
static void* g_originalSubmitSystemMessage = nullptr;
static void* g_originalPremiumStateUpdate = nullptr;
static volatile LONG g_lotteryNoticeOverrideEnabled = 0;
static volatile LONG g_lotteryNoticeReplacementArmed = 0;
static volatile LONG g_pendingLotteryInventoryNoticeArmed = 0;
static volatile LONG g_pendingLotteryInventoryNoticeItemId = 0;
static volatile LONG g_pendingLotteryInventoryNoticeContext = 0;
static volatile LONG g_pendingLotteryBatchCapTransition = 0;

static constexpr uintptr_t InventoryNoticeWrapperOffset = 0x00DA9590;
static constexpr uintptr_t DirectInventoryNoticeWrapperOffset = 0x00DA95D0;
static constexpr uintptr_t SubmitSystemMessageJumpOffset = 0x0189CFB0;
static constexpr uintptr_t SubmitSystemMessageDestinationOffset = 0x04934C20;
static constexpr uintptr_t PremiumStateUpdateOffset = 0x00BA0690;
static constexpr uintptr_t PremiumStateSingletonOffset = 0x02C79BA4;
static constexpr uintptr_t InventoryNoticeContextOwnerOffset = 0x02C91F40;
static constexpr uintptr_t ResolveInventoryNoticeContextOffset = 0x010A2D10;
static constexpr unsigned int LotteryResultSubmitReturnOffset = 0x008F041F;
static constexpr unsigned int InventoryNoticeReturnOffset = 0x00927C37;
static constexpr unsigned int DirectInventoryNoticeReturnOffset = 0x008F065A;
static constexpr SIZE_T ItemObjectTemplateIdOffset = 0x20;
static constexpr SIZE_T LotteryResultObjectKindOffset = 0x04;
static constexpr SIZE_T LotteryResultItemTemplateIdOffset = 0x288;
static constexpr SIZE_T LotteryResultDisplayValueOffset = 0x28C;
static constexpr unsigned int LotteryResultObjectKindCommon = 2;
static constexpr SIZE_T LotteryBatchItemOffset = 0x08;
static constexpr SIZE_T LotteryBatchModeOffset = 0x0C;
static constexpr SIZE_T LotteryBatchRemainingOffset = 0x10;
static constexpr SIZE_T LotteryPhaseFlagOffset = 0x15;
static constexpr SIZE_T LotteryDoubleUsedCountOffset = 0x2A;
static constexpr unsigned int LotteryDoubleDailyLimit = 8;

static unsigned int ReadObjectField(const void* object, SIZE_T offset);
static unsigned int ResolveInventoryNoticeContext();
static void ClearPendingLotteryInventoryNotice();

static void* InstallRelativeJumpHook(void* target, void* detour, void* expectedDestination)
{
    if (target == nullptr || detour == nullptr || expectedDestination == nullptr ||
        *static_cast<BYTE*>(target) != 0xE9)
        return nullptr;

    auto targetBytes = static_cast<BYTE*>(target);
    const auto displacement = *reinterpret_cast<int32_t*>(targetBytes + 1);
    void* originalDestination = targetBytes + 5 + displacement;
    if (originalDestination != expectedDestination)
        return nullptr;

    DWORD oldProtection = 0;
    if (!VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &oldProtection))
        return nullptr;

    InterlockedExchange(
        reinterpret_cast<volatile LONG*>(targetBytes + 1),
        static_cast<LONG>(static_cast<BYTE*>(detour) - (targetBytes + 5)));
    FlushInstructionCache(GetCurrentProcess(), target, 5);

    DWORD ignored = 0;
    VirtualProtect(target, 5, oldProtection, &ignored);
    return originalDestination;
}

void __cdecl ProxyGameLog(int a1, wchar_t* source_path, wchar_t* function_name, int logType, wchar_t* Format, ...)
{
    wchar_t Buffer[512] = { 0 };
    wchar_t* dynamicBuffer = NULL;
    wchar_t* outputBuffer = Buffer;
    int bufferSize = _countof(Buffer);

    va_list ArgList;
    va_start(ArgList, Format);

    int result = _vswprintf_c_l(Buffer, bufferSize, Format, 0, ArgList);

    if (result < 0) {
        va_end(ArgList);
        va_start(ArgList, Format);

        int neededSize = _vscwprintf_l(Format, 0, ArgList) + 1;

        if (neededSize > 0) {
            dynamicBuffer = (wchar_t*)malloc(neededSize * sizeof(wchar_t));
            if (dynamicBuffer) {
                va_end(ArgList);
                va_start(ArgList, Format);
                _vswprintf_c_l(dynamicBuffer, neededSize, Format, 0, ArgList);
                outputBuffer = dynamicBuffer;
            }
        }
    }

    va_end(ArgList);

    if (outputBuffer) {
        AppendFileLogFormatLine(L"GameLog.log", L"[%s] [%d] [%s]", function_name, logType, outputBuffer);
    }

    if (dynamicBuffer) {
        free(dynamicBuffer);
    }
}

int __fastcall Proxy_CipherEncrypt(void* This, void* NotUsed, int packet_type, char* input, int in_size, char* out_put, int* out_size)
{
    *(int*)(input - 13 + 3) = in_size + 13;

    *out_size = in_size;
    memcpy(out_put, input, in_size);
    return 1;
}

void __cdecl Proxy_InventoryNoticeWrapper(void* item, unsigned int quantity, unsigned int context, int auxiliaryValue)
{
    const unsigned int returnOffset =
        static_cast<unsigned int>(reinterpret_cast<uintptr_t>(_ReturnAddress()) - dnf_base);
    if (InterlockedCompareExchange(&g_lotteryNoticeOverrideEnabled, 0, 0) == 1 &&
        returnOffset == InventoryNoticeReturnOffset &&
        quantity == 1 &&
        InterlockedCompareExchange(&g_pendingLotteryInventoryNoticeArmed, 0, 0) == 1)
    {
        const unsigned int itemTemplateId = ReadObjectField(item, ItemObjectTemplateIdOffset);
        if (itemTemplateId != 0 &&
            itemTemplateId != 0xFFFFFFFF &&
            itemTemplateId == static_cast<unsigned int>(
                InterlockedCompareExchange(&g_pendingLotteryInventoryNoticeItemId, 0, 0)) &&
            context == static_cast<unsigned int>(
                InterlockedCompareExchange(&g_pendingLotteryInventoryNoticeContext, 0, 0)) &&
            InterlockedCompareExchange(&g_pendingLotteryInventoryNoticeArmed, 0, 1) == 1)
        {
            ClearPendingLotteryInventoryNotice();
            return;
        }
    }

    using InventoryNoticeWrapper = void(__cdecl*)(void*, unsigned int, unsigned int, int);
    reinterpret_cast<InventoryNoticeWrapper>(g_originalInventoryNoticeWrapper)(
        item,
        quantity,
        context,
        auxiliaryValue);
}

void __cdecl Proxy_DirectInventoryNoticeWrapper(void* item, unsigned int quantity, unsigned int context)
{
    const unsigned int returnOffset =
        static_cast<unsigned int>(reinterpret_cast<uintptr_t>(_ReturnAddress()) - dnf_base);
    if (InterlockedCompareExchange(&g_lotteryNoticeOverrideEnabled, 0, 0) == 1 &&
        InterlockedCompareExchange(&g_lotteryNoticeReplacementArmed, 0, 0) == 1 &&
        returnOffset == DirectInventoryNoticeReturnOffset)
        return;

    using DirectInventoryNoticeWrapper = void(__cdecl*)(void*, unsigned int, unsigned int);
    reinterpret_cast<DirectInventoryNoticeWrapper>(g_originalDirectInventoryNoticeWrapper)(
        item,
        quantity,
        context);
}

static unsigned int ReadObjectField(const void* object, SIZE_T offset)
{
    unsigned int value = 0;
    SIZE_T bytesRead = 0;
    if (object != nullptr)
        ReadProcessMemory(
            GetCurrentProcess(),
            static_cast<const BYTE*>(object) + offset,
            &value,
            sizeof(value),
            &bytesRead);
    return bytesRead == sizeof(value) ? value : 0xFFFFFFFF;
}

static unsigned int ResolveInventoryNoticeContext()
{
    const unsigned int owner = ReadObjectField(
        reinterpret_cast<const void*>(dnf_base + InventoryNoticeContextOwnerOffset),
        0);
    if (owner == 0 || owner == 0xFFFFFFFF)
        return 0;

    using ResolveContext = unsigned int(__thiscall*)(void*);
    return reinterpret_cast<ResolveContext>(
        dnf_base + ResolveInventoryNoticeContextOffset)(reinterpret_cast<void*>(owner));
}

static void ClearPendingLotteryInventoryNotice()
{
    InterlockedExchange(&g_pendingLotteryInventoryNoticeArmed, 0);
    InterlockedExchange(&g_pendingLotteryInventoryNoticeItemId, 0);
    InterlockedExchange(&g_pendingLotteryInventoryNoticeContext, 0);
}

static void ArmLotteryBatchCapTransition()
{
    const unsigned int singleton = ReadObjectField(
        reinterpret_cast<const void*>(dnf_base + PremiumStateSingletonOffset),
        0);
    const void* state = singleton == 0 || singleton == 0xFFFFFFFF
        ? nullptr
        : reinterpret_cast<const void*>(singleton);
    const unsigned int batchItem = ReadObjectField(state, LotteryBatchItemOffset);
    const unsigned int batchMode = ReadObjectField(state, LotteryBatchModeOffset) & 0xFF;
    const unsigned int batchRemaining = ReadObjectField(state, LotteryBatchRemainingOffset);
    const unsigned int phaseFlag = ReadObjectField(state, LotteryPhaseFlagOffset) & 0xFF;
    const unsigned int usedCount = ReadObjectField(state, LotteryDoubleUsedCountOffset);

    if (batchItem != 0 &&
        batchItem != 0xFFFFFFFF &&
        batchMode == 1 &&
        batchRemaining != 0xFFFFFFFF &&
        batchRemaining > 0 &&
        phaseFlag == 1 &&
        usedCount != 0xFFFFFFFF &&
        usedCount < LotteryDoubleDailyLimit)
    {
        InterlockedExchange(&g_pendingLotteryBatchCapTransition, 1);
    }
}

void __fastcall Proxy_PremiumStateUpdate(void* state, void*, const void* serviceData)
{
    using PremiumStateUpdate = void(__thiscall*)(void*, const void*);
    reinterpret_cast<PremiumStateUpdate>(g_originalPremiumStateUpdate)(state, serviceData);

    // A double-result inventory refresh, when present, precedes this native premium refresh.
    ClearPendingLotteryInventoryNotice();

    if (InterlockedExchange(&g_pendingLotteryBatchCapTransition, 0) != 1 || state == nullptr)
        return;

    const unsigned int singleton = ReadObjectField(
        reinterpret_cast<const void*>(dnf_base + PremiumStateSingletonOffset),
        0);
    const unsigned int batchItem = ReadObjectField(state, LotteryBatchItemOffset);
    const unsigned int batchMode = ReadObjectField(state, LotteryBatchModeOffset) & 0xFF;
    const unsigned int batchRemaining = ReadObjectField(state, LotteryBatchRemainingOffset);
    const unsigned int phaseFlag = ReadObjectField(state, LotteryPhaseFlagOffset) & 0xFF;
    const unsigned int usedCount = ReadObjectField(state, LotteryDoubleUsedCountOffset);
    if (reinterpret_cast<unsigned int>(state) != singleton ||
        batchItem == 0 ||
        batchItem == 0xFFFFFFFF ||
        batchMode != 1 ||
        batchRemaining == 0 ||
        batchRemaining == 0xFFFFFFFF ||
        phaseFlag != 1 ||
        usedCount == 0xFFFFFFFF ||
        usedCount < LotteryDoubleDailyLimit)
    {
        return;
    }

    *reinterpret_cast<volatile BYTE*>(
        static_cast<BYTE*>(state) + LotteryPhaseFlagOffset) = 0;
}

void __cdecl ObserveLotteryResultSubmit(const unsigned int* stack)
{
    if (stack == nullptr)
        return;

    const unsigned int returnOffset = stack[0] - static_cast<unsigned int>(dnf_base);
    if (returnOffset != LotteryResultSubmitReturnOffset)
        return;

    if (InterlockedCompareExchange(&g_lotteryNoticeOverrideEnabled, 0, 0) != 1)
        return;

    InterlockedExchange(&g_lotteryNoticeReplacementArmed, 0);
    ClearPendingLotteryInventoryNotice();
    InterlockedExchange(&g_pendingLotteryBatchCapTransition, 0);
    if (stack[1] != 0x101 || stack[3] != 1)
        return;

    const void* resultObject = reinterpret_cast<void*>(stack[2]);
    const unsigned int objectKind = ReadObjectField(resultObject, LotteryResultObjectKindOffset);
    unsigned int itemTemplateId = 0;
    unsigned int displayValue = 0;
    bool hasVerifiedLotteryItem = false;
    if (objectKind == LotteryResultObjectKindCommon)
    {
        itemTemplateId = ReadObjectField(resultObject, LotteryResultItemTemplateIdOffset);
        displayValue = ReadObjectField(resultObject, LotteryResultDisplayValueOffset);
        hasVerifiedLotteryItem =
            itemTemplateId != 0 &&
            itemTemplateId != 0xFFFFFFFF;
    }

    const unsigned int directItemTemplateId = ReadObjectField(resultObject, ItemObjectTemplateIdOffset);
    const unsigned int quantity = displayValue == 2 ? 2 : 1;
    const bool isVerifiedLotteryNoticeResult =
        hasVerifiedLotteryItem &&
        (displayValue == 1 || displayValue == 2) &&
        directItemTemplateId == itemTemplateId;
    if (isVerifiedLotteryNoticeResult &&
        g_originalDirectInventoryNoticeWrapper != nullptr &&
        InterlockedCompareExchange(&g_lotteryNoticeOverrideEnabled, 0, 0) == 1)
    {
        const unsigned int context = ResolveInventoryNoticeContext();
        if (context != 0)
        {
            using DirectInventoryNoticeWrapper = void(__cdecl*)(void*, unsigned int, unsigned int);
            reinterpret_cast<DirectInventoryNoticeWrapper>(g_originalDirectInventoryNoticeWrapper)(
                const_cast<void*>(resultObject),
                quantity,
                context);
            InterlockedExchange(&g_lotteryNoticeReplacementArmed, 1);
            if (displayValue == 2)
            {
                InterlockedExchange(
                    &g_pendingLotteryInventoryNoticeItemId,
                    static_cast<LONG>(itemTemplateId));
                InterlockedExchange(
                    &g_pendingLotteryInventoryNoticeContext,
                    static_cast<LONG>(context));
                InterlockedExchange(&g_pendingLotteryInventoryNoticeArmed, 1);
            }
        }
    }

    if (hasVerifiedLotteryItem)
        ArmLotteryBatchCapTransition();
}

__declspec(naked) void Proxy_SubmitSystemMessage()
{
    __asm
    {
        pushfd
        pushad
        lea eax, [esp + 36]
        push eax
        call ObserveLotteryResultSubmit
        add esp, 4
        popad
        popfd
        jmp dword ptr [g_originalSubmitSystemMessage]
    }
}

static uintptr_t g_Ptr_SendMessageW = 0;
LRESULT WINAPI Proxy_SendMessageW(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam)
{
    if (Msg == 0x111 && wParam == 0x19F && lParam == 0)
        return 0;
    auto original = reinterpret_cast<decltype(&Proxy_SendMessageW)>(Hook_GetTrampoline(g_Ptr_SendMessageW));
    return original(hWnd, Msg, wParam, lParam);
}

unsigned int DelayHook(void*)
{
    do
    {
        Sleep(100);
    } while (nullptr == GetModuleHandleW(L"GameGaurd.dll"));

    Sleep(1000);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01C11360), Proxy_CipherEncrypt);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9700), ProxyGameLog);

    static const BYTE ExpectedNoticeWrapperPrologue[] = { 0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x1C };
    static const BYTE ExpectedPremiumStateUpdatePrologue[] = {
        0x55, 0x8B, 0xEC, 0x53, 0x56, 0x8B, 0x75, 0x08, 0x8B, 0xD9
    };
    void* directNoticeWrapper = reinterpret_cast<void*>(dnf_base + DirectInventoryNoticeWrapperOffset);
    if (memcmp(directNoticeWrapper, ExpectedNoticeWrapperPrologue, sizeof(ExpectedNoticeWrapperPrologue)) == 0 &&
        Hook_Inline(directNoticeWrapper, Proxy_DirectInventoryNoticeWrapper))
    {
        g_originalDirectInventoryNoticeWrapper = Hook_GetTrampoline(
            reinterpret_cast<uintptr_t>(directNoticeWrapper));
    }

    void* noticeWrapper = reinterpret_cast<void*>(dnf_base + InventoryNoticeWrapperOffset);
    if (memcmp(noticeWrapper, ExpectedNoticeWrapperPrologue, sizeof(ExpectedNoticeWrapperPrologue)) == 0 &&
        Hook_Inline(noticeWrapper, Proxy_InventoryNoticeWrapper))
    {
        g_originalInventoryNoticeWrapper = Hook_GetTrampoline(reinterpret_cast<uintptr_t>(noticeWrapper));
    }

    if (g_originalDirectInventoryNoticeWrapper != nullptr &&
        g_originalInventoryNoticeWrapper != nullptr)
    {
        g_originalSubmitSystemMessage = InstallRelativeJumpHook(
            reinterpret_cast<void*>(dnf_base + SubmitSystemMessageJumpOffset),
            Proxy_SubmitSystemMessage,
            reinterpret_cast<void*>(dnf_base + SubmitSystemMessageDestinationOffset));
    }

    if (g_originalSubmitSystemMessage != nullptr)
    {
        void* premiumStateUpdate = reinterpret_cast<void*>(dnf_base + PremiumStateUpdateOffset);
        if (memcmp(
                premiumStateUpdate,
                ExpectedPremiumStateUpdatePrologue,
                sizeof(ExpectedPremiumStateUpdatePrologue)) == 0 &&
            Hook_Inline(premiumStateUpdate, Proxy_PremiumStateUpdate))
        {
            g_originalPremiumStateUpdate = Hook_GetTrampoline(
                reinterpret_cast<uintptr_t>(premiumStateUpdate));
        }
    }

    if (g_originalSubmitSystemMessage != nullptr &&
        g_originalPremiumStateUpdate != nullptr)
    {
        InterlockedExchange(&g_lotteryNoticeOverrideEnabled, 1);
    }

    return 0;
}

void PluginEntry()
{
    dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));

    DeleteFileW(L"GameLog.log");

    CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE)DelayHook, NULL, 0, NULL);

    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9700), ProxyGameLog);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9800), ProxyGameLog);

    auto user32 = GetModuleHandleW(L"user32.dll");
    if (user32)
    {
        g_Ptr_SendMessageW = (uintptr_t)GetProcAddress(user32, "SendMessageW");
        Hook_Inline(reinterpret_cast<void*>(g_Ptr_SendMessageW), Proxy_SendMessageW);
    }
}

uintptr_t g_Ptr_GetStartupInfoW = 0;
VOID WINAPI Proxy_GetStartupInfoW(_Out_ LPSTARTUPINFOW lpStartupInfo)
{
    auto return_addr = (uintptr_t)_ReturnAddress();
    if (return_addr == dnf_base + 0x04AE71A5)
        PluginEntry();

    auto orifunc = reinterpret_cast<decltype(&Proxy_GetStartupInfoW)>(Hook_GetTrampoline(g_Ptr_GetStartupInfoW));
    orifunc(lpStartupInfo);
}

void JPEntry()
{
    dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));

    auto kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (kernel32)
    {
        g_Ptr_GetStartupInfoW = (uintptr_t)GetProcAddress(kernel32, "GetStartupInfoW");
        Hook_Inline(reinterpret_cast<void*>(g_Ptr_GetStartupInfoW), Proxy_GetStartupInfoW);
    }
}
