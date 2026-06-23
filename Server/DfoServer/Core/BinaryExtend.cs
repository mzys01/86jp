using System;
using System.Text;

namespace DfoServer.Core
{
    public static class BinaryExtend
    {
        public static byte[] ToBytes(this string str)
        {
            return Encoding.ASCII.GetBytes(str);
        }

        public static byte[] ToBin(this string hexString, bool stat = true)
        {
            if (stat) hexString = hexString.Replace("0x", "").Replace(" ", "").Replace("\r\n", "");
            if (hexString.Length % 2 != 0)
                hexString = "0" + hexString;
            byte[] returnBytes = new byte[hexString.Length / 2];
            for (int i = 0; i < returnBytes.Length; i++)
            {
                returnBytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            }
            return returnBytes;
        }
    }
}
