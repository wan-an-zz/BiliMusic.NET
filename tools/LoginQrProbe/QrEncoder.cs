using System.Text;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>
/// 极简二维码编码器：只实现探针需要的那一档（版本 5、纠错等级 L、字节模式，容量 106 字节）。
/// </summary>
/// <remarks>
/// <para>
/// B 站扫码登录的二维码内容是约 60 字符的 URL，版本 5-L 足够容纳。
/// 之所以自带一份而不引第三方包：探针需要保持零外部依赖，能在离线环境直接跑起来。
/// </para>
/// <para>
/// 实现要点（对不上任何一条都会渲染出扫不动的图）：
/// 字节模式、GF(256) 上的 Reed-Solomon 纠错、8 种掩码按惩罚分择优、格式信息用 BCH(15,5) 并在写入前异或 0x5412。
/// </para>
/// </remarks>
internal static class QrEncoder
{
    /// <summary>版本 5-L 的数据码字数。</summary>
    private const int DataCodewords = 108;

    /// <summary>版本 5-L 的纠错码字数。</summary>
    private const int EcCodewords = 26;

    /// <summary>版本 5 的模块边长（21 + 4 × (版本 - 1)）。</summary>
    private const int Size = 37;

    /// <summary>GF(256) 的本原多项式（x^8 + x^4 + x^3 + x^2 + 1）。</summary>
    private const int Primitive = 0x11D;

    /// <summary>格式信息异或掩码（规范规定，防止全 0 格式串）。</summary>
    private const int FormatXorMask = 0x5412;

    /// <summary>格式信息的 BCH(15,5) 生成多项式。</summary>
    private const int Generator = 0x537;

    /// <summary>字节模式指示符（4 位）。</summary>
    private const int ModeByte = 4;

    /// <summary>版本 5 的字符计数指示符长度为 8 位。</summary>
    private const int CharCountBits = 8;

    /// <summary>
    /// 把文本编码成二维码模块矩阵。
    /// </summary>
    /// <param name="text">要编码的文本（按 UTF-8 计字节）。</param>
    /// <returns>二维模块矩阵，<see langword="true"/> 表示深色模块。</returns>
    /// <exception cref="ArgumentException">文本超出本实现的容量。</exception>
    public static bool[,] Encode(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > DataCodewords - 2)
        {
            throw new ArgumentException(
                $"文本 {payload.Length} 字节，超出本实现容量（{DataCodewords - 2} 字节）。", nameof(text));
        }

        var data = BuildDataCodewords(payload);
        var ecc = ComputeErrorCorrection(data);

        // 交错：数据码字 + 纠错码字按序填入（单块，所以就是直接拼接）。
        var codewords = new byte[DataCodewords + EcCodewords];
        data.CopyTo(codewords, 0);
        ecc.CopyTo(codewords, DataCodewords);

        var best = default(bool[,])!;
        var bestPenalty = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            var matrix = BuildMatrix(codewords, mask);
            var penalty = ComputePenalty(matrix);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = matrix;
            }
        }

        return best;
    }

    /// <summary>
    /// 生成数据码字：模式指示符 + 字符计数 + 数据 + 终止符 + 补齐 + 填充字节。
    /// </summary>
    /// <param name="payload">UTF-8 字节。</param>
    /// <returns>108 个数据码字。</returns>
    private static byte[] BuildDataCodewords(byte[] payload)
    {
        var bits = new BitBuffer();
        bits.Append(ModeByte, 4);
        bits.Append(payload.Length, CharCountBits);
        foreach (var b in payload)
        {
            bits.Append(b, 8);
        }

        var capacityBits = DataCodewords * 8;
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));   // 终止符
        bits.Append(0, (8 - (bits.Length % 8)) % 8);               // 补齐到字节边界

        var result = new byte[DataCodewords];
        bits.ToBytes(result);

        // 交替填充 0xEC / 0x11 直到占满。
        var written = bits.Length / 8;
        var pad = new byte[] { 0xEC, 0x11 };
        for (var i = written; i < DataCodewords; i++)
        {
            result[i] = pad[(i - written) % 2];
        }

        return result;
    }

    /// <summary>
    /// 计算 Reed-Solomon 纠错码字。
    /// </summary>
    /// <param name="data">数据码字。</param>
    /// <returns>26 个纠错码字。</returns>
    private static byte[] ComputeErrorCorrection(byte[] data)
    {
        var generator = BuildGenerator(EcCodewords);
        var remainder = new byte[EcCodewords];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, EcCodewords - 1);
            remainder[EcCodewords - 1] = 0;

            for (var i = 0; i < EcCodewords; i++)
            {
                remainder[i] ^= GfMultiply(generator[i], factor);
            }
        }

        return remainder;
    }

    /// <summary>
    /// 生成多项式 ∏(x - α^i)，i 从 0 到 degree-1。
    /// </summary>
    /// <param name="degree">纠错码字数。</param>
    /// <returns>除首项外的多项式系数。</returns>
    private static byte[] BuildGenerator(int degree)
    {
        var result = new byte[] { 1 };

        for (var i = 0; i < degree; i++)
        {
            var next = new byte[result.Length + 1];
            for (var j = 0; j < result.Length; j++)
            {
                next[j] ^= result[j];
                next[j + 1] ^= GfMultiply(result[j], GfPow(2, i));
            }

            result = next;
        }

        // result[0] 恒为 1，去掉它。
        return result[1..];
    }

    /// <summary>GF(256) 乘法。</summary>
    private static byte GfMultiply(byte a, byte b)
    {
        var result = 0;
        var x = (int)a;
        var y = (int)b;

        while (y != 0)
        {
            if ((y & 1) != 0)
            {
                result ^= x;
            }

            x <<= 1;
            if ((x & 0x100) != 0)
            {
                x ^= Primitive;
            }

            y >>= 1;
        }

        return (byte)result;
    }

    /// <summary>GF(256) 幂运算。</summary>
    private static byte GfPow(byte value, int exponent)
    {
        var result = (byte)1;
        for (var i = 0; i < exponent; i++)
        {
            result = GfMultiply(result, value);
        }

        return result;
    }

    /// <summary>
    /// 构造完整矩阵：功能图案 → 写入数据 → 应用掩码 → 写入格式信息。
    /// </summary>
    /// <param name="codewords">数据与纠错码字。</param>
    /// <param name="mask">掩码编号 0-7。</param>
    /// <returns>模块矩阵。</returns>
    private static bool[,] BuildMatrix(byte[] codewords, int mask)
    {
        var modules = new bool[Size, Size];
        var reserved = new bool[Size, Size];

        DrawFinder(modules, reserved, 0, 0);
        DrawFinder(modules, reserved, Size - 7, 0);
        DrawFinder(modules, reserved, 0, Size - 7);
        DrawAlignment(modules, reserved, 30, 30);
        DrawTiming(modules, reserved);
        ReserveFormatAreas(reserved);
        modules[Size - 8, 8] = true;   // 固定的深色模块

        PlaceData(modules, reserved, codewords, mask);

        DrawFormat(modules, mask);

        return modules;
    }

    /// <summary>绘制定位图案（含分隔符）。</summary>
    private static void DrawFinder(bool[,] modules, bool[,] reserved, int left, int top)
    {
        for (var dy = -1; dy <= 7; dy++)
        {
            for (var dx = -1; dx <= 7; dx++)
            {
                var x = left + dx;
                var y = top + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size)
                {
                    continue;
                }

                var inRing = dx is >= 0 and <= 6 && dy is >= 0 and <= 6;
                var dark = inRing && (dx == 0 || dx == 6 || dy == 0 || dy == 6 || (dx is >= 2 and <= 4 && dy is >= 2 and <= 4));

                modules[y, x] = dark;
                reserved[y, x] = true;
            }
        }
    }

    /// <summary>绘制校正图案。</summary>
    private static void DrawAlignment(bool[,] modules, bool[,] reserved, int centerX, int centerY)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = centerX + dx;
                var y = centerY + dy;
                var dark = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;

                modules[y, x] = dark;
                reserved[y, x] = true;
            }
        }
    }

    /// <summary>绘制定时图案。</summary>
    private static void DrawTiming(bool[,] modules, bool[,] reserved)
    {
        for (var i = 8; i < Size - 8; i++)
        {
            var dark = i % 2 == 0;
            modules[6, i] = dark;
            reserved[6, i] = true;
            modules[i, 6] = dark;
            reserved[i, 6] = true;
        }
    }

    /// <summary>预留格式信息所在模块（数据不能占用）。</summary>
    private static void ReserveFormatAreas(bool[,] reserved)
    {
        for (var i = 0; i < 9; i++)
        {
            reserved[8, i] = true;      // 第 8 行左侧（跳过定时图案所在的第 6 列不影响）
            reserved[i, 8] = true;      // 第 8 列上方
        }

        reserved[8, 6] = true;
        reserved[6, 8] = true;

        for (var i = 0; i < 8; i++)
        {
            reserved[8, Size - 1 - i] = true;   // 第 8 行右侧
            reserved[Size - 1 - i, 8] = true;   // 第 8 列下方
        }
    }

    /// <summary>
    /// 按标准顺序（右下起、两列一组、蛇形向上）写入数据位并应用掩码。
    /// </summary>
    private static void PlaceData(bool[,] modules, bool[,] reserved, byte[] codewords, int mask)
    {
        var bitIndex = 0;
        var totalBits = codewords.Length * 8;

        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;   // 跳过垂直定时图案所在的第 6 列
            }

            for (var vert = 0; vert < Size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;

                    if (reserved[y, x])
                    {
                        continue;
                    }

                    var dark = false;
                    if (bitIndex < totalBits)
                    {
                        dark = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        bitIndex++;
                    }

                    modules[y, x] = dark ^ MaskBit(mask, x, y);
                }
            }
        }
    }

    /// <summary>掩码条件。</summary>
    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
        _ => false,
    };

    /// <summary>
    /// 写入两份格式信息：纠错等级 L 与掩码编号经 BCH(15,5) 编码后异或 0x5412。
    /// </summary>
    /// <remarks>
    /// 算法等价于"data·x^10 mod 0x537"，这里用教科书的移位寄存器写法：从高位到低位，逢 1 就异或生成多项式。
    /// 曾经写成"左移 1 位后判断第 9 位"的变体，算出来的格式串不匹配任何合法码字，扫不出来。
    /// </remarks>
    private static void DrawFormat(bool[,] modules, int mask)
    {
        // 纠错等级 L 的指示符是 1（两位），高位在前：data = 0b01_mask。
        var data = (1 << 3) | mask;
        var remainder = data << 10;

        for (var i = 14; i >= 10; i--)
        {
            if (((remainder >> i) & 1) != 0)
            {
                remainder ^= Generator << (i - 10);
            }
        }

        var bits = ((data << 10) | remainder) ^ FormatXorMask;

        for (var i = 0; i <= 5; i++)
        {
            modules[8, i] = GetBit(bits, i);
        }

        modules[8, 7] = GetBit(bits, 6);
        modules[8, 8] = GetBit(bits, 7);
        modules[7, 8] = GetBit(bits, 8);

        for (var i = 9; i < 15; i++)
        {
            modules[14 - i, 8] = GetBit(bits, i);
        }

        for (var i = 0; i < 8; i++)
        {
            modules[Size - 1 - i, 8] = GetBit(bits, i);
        }

        for (var i = 8; i < 15; i++)
        {
            modules[8, Size - 15 + i] = GetBit(bits, i);
        }
    }

    /// <summary>取格式串的第 <paramref name="index"/> 位。</summary>
    private static bool GetBit(int value, int index) => ((value >> index) & 1) != 0;

    /// <summary>
    /// 按规范计算掩码惩罚分，分数越低越好。
    /// </summary>
    /// <param name="modules">模块矩阵。</param>
    /// <returns>惩罚分。</returns>
    private static int ComputePenalty(bool[,] modules)
    {
        var penalty = 0;

        // 规则 1：行/列中连续同色模块。
        for (var i = 0; i < Size; i++)
        {
            penalty += LinePenalty(i, true);
            penalty += LinePenalty(i, false);
        }

        // 规则 2：2×2 同色块。
        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var c = modules[y, x];
                if (modules[y, x + 1] == c && modules[y + 1, x] == c && modules[y + 1, x + 1] == c)
                {
                    penalty += 3;
                }
            }
        }

        // 规则 4：深色模块比例偏离 50%。
        var dark = 0;
        foreach (var module in modules)
        {
            if (module)
            {
                dark++;
            }
        }

        var percent = dark * 100 / (Size * Size);
        penalty += Math.Abs(percent - 50) / 5 * 10;

        return penalty;

        int LinePenalty(int index, bool horizontal)
        {
            var score = 0;
            var run = 1;

            for (var i = 1; i < Size; i++)
            {
                var current = horizontal ? modules[index, i] : modules[i, index];
                var previous = horizontal ? modules[index, i - 1] : modules[i - 1, index];

                if (current == previous)
                {
                    run++;
                }
                else
                {
                    if (run >= 5)
                    {
                        score += 3 + (run - 5);
                    }

                    run = 1;
                }
            }

            if (run >= 5)
            {
                score += 3 + (run - 5);
            }

            return score;
        }
    }

    /// <summary>按位写入的缓冲区。</summary>
    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        /// <summary>已写入的位数。</summary>
        public int Length => _bits.Count;

        /// <summary>追加若干位（高位在前）。</summary>
        /// <param name="value">数值。</param>
        /// <param name="count">位数。</param>
        public void Append(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        /// <summary>把已写入的位按字节填入目标数组。</summary>
        /// <param name="target">目标数组。</param>
        public void ToBytes(byte[] target)
        {
            for (var i = 0; i < _bits.Count / 8 && i < target.Length; i++)
            {
                var value = 0;
                for (var j = 0; j < 8; j++)
                {
                    value = (value << 1) | (_bits[i * 8 + j] ? 1 : 0);
                }

                target[i] = (byte)value;
            }
        }
    }
}
