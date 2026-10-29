namespace HsHelper.Cards;

/// <summary>Version-one deckstring codec, including the optional sideboard extension.</summary>
public static class DeckCode
{
    private const int MaximumEntries = 1000;

    public static DeckDefinition Decode(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var codeLines = input.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).ToArray();
        if (codeLines.Length != 1 || codeLines[0].Length > 16384)
            throw new FormatException("输入应包含一条牌码，可附带以 # 开头的说明。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(codeLines[0]); }
        catch (FormatException error) { throw new FormatException("无效的 Base64 牌码。", error); }
        var reader = new DeckReader(bytes);
        if (reader.Read() != 0) throw new FormatException("无效的牌码保留字节。");
        if (reader.Read() != 1) throw new NotSupportedException("只支持版本 1 牌码。");
        var format = reader.Read();
        if (format is < 1 or > 4) throw new NotSupportedException($"不支持的牌码模式：{format}");
        var heroes = new List<int>();
        var heroCount = reader.ReadCount();
        if (heroCount != 1) throw new NotSupportedException("当前只支持单英雄套牌。");
        for (var i = 0; i < heroCount; i++) heroes.Add(reader.ReadPositive());
        var cards = ReadCards(reader, false).Select(x => new DeckCard(x.Card, x.Count)).ToArray();
        EnsureUnique(cards, "主牌库");
        var sideboards = Array.Empty<DeckSideboard>();
        // Old deckstrings may end immediately after the main-deck block.
        if (!reader.AtEnd)
        {
            var flag = reader.Read();
            if (flag is not (0 or 1)) throw new NotSupportedException("未知的牌码扩展标记。");
            if (flag == 1)
            {
                sideboards = ReadCards(reader, true).GroupBy(x => x.Owner).OrderBy(x => x.Key)
                    .Select(group => new DeckSideboard(group.Key, Array.AsReadOnly(group.Select(x => new DeckCard(x.Card, x.Count)).OrderBy(x => x.DbfId).ToArray())))
                    .ToArray();
                foreach (var sideboard in sideboards) EnsureUnique(sideboard.Cards, "边栏");
            }
        }
        if (!reader.AtEnd) throw new NotSupportedException("牌码包含未识别的扩展数据，已拒绝避免丢失信息。");
        return new DeckDefinition(format, heroes.AsReadOnly(), Array.AsReadOnly(cards.OrderBy(x => x.DbfId).ToArray()), Array.AsReadOnly(sideboards));
    }

    public static string Encode(DeckDefinition deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        if (deck.Format is < 1 or > 4) throw new NotSupportedException("未知的套牌模式。");
        if (deck.Heroes.Count != 1 || deck.Heroes[0] <= 0) throw new ArgumentException("必须指定一个有效的英雄。", nameof(deck));
        EnsureUnique(deck.Cards, "主牌库");
        if (deck.Sideboards.Select(x => x.OwnerDbfId).Distinct().Count() != deck.Sideboards.Count)
            throw new ArgumentException("边栏拥有者不能重复。", nameof(deck));
        using var stream = new MemoryStream();
        Write(stream, 0);
        Write(stream, 1);
        Write(stream, deck.Format);
        Write(stream, 1);
        Write(stream, deck.Heroes[0]);
        WriteCards(stream, deck.Cards.Select(x => (x.DbfId, x.Count, 0)), false);
        var sideboards = deck.Sideboards.Where(x => x.Cards.Count != 0).ToArray();
        Write(stream, sideboards.Length == 0 ? 0 : 1);
        if (sideboards.Length > 0)
        {
            foreach (var board in sideboards)
            {
                if (board.OwnerDbfId <= 0) throw new ArgumentException("边栏拥有者必须为有效卡牌标识。", nameof(deck));
                EnsureUnique(board.Cards, "边栏");
            }
            WriteCards(stream, sideboards.SelectMany(x => x.Cards.Select(c => (c.DbfId, c.Count, x.OwnerDbfId))), true);
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    private static List<(int Card, int Count, int Owner)> ReadCards(DeckReader reader, bool sideboard)
    {
        var result = new List<(int Card, int Count, int Owner)>();
        for (var group = 1; group <= 3; group++)
        {
            var length = reader.ReadCount();
            if (result.Count + length > MaximumEntries) throw new FormatException("牌码条目过多。");
            for (var i = 0; i < length; i++)
            {
                var id = reader.ReadPositive();
                var count = group == 3 ? reader.ReadPositive() : group;
                if (count > MaximumEntries) throw new FormatException("单牌数量超出支持范围。");
                var owner = sideboard ? reader.ReadPositive() : 0;
                result.Add((id, count, owner));
            }
        }
        return result;
    }

    private static void WriteCards(Stream stream, IEnumerable<(int Id, int Count, int Owner)> input, bool sideboard)
    {
        var cards = input.ToArray();
        if (cards.Length > MaximumEntries) throw new ArgumentException("牌码条目过多。");
        for (var group = 1; group <= 3; group++)
        {
            var selected = cards.Where(x => group == 3 ? x.Count >= 3 : x.Count == group).OrderBy(x => x.Owner).ThenBy(x => x.Id).ToArray();
            Write(stream, selected.Length);
            foreach (var card in selected)
            {
                Write(stream, card.Id);
                if (group == 3) Write(stream, card.Count);
                if (sideboard) Write(stream, card.Owner);
            }
        }
    }

    private static void EnsureUnique(IReadOnlyList<DeckCard> cards, string section)
    {
        if (cards.Count > MaximumEntries || cards.Any(x => x.DbfId <= 0 || x.Count is < 1 or > MaximumEntries) || cards.Select(x => x.DbfId).Distinct().Count() != cards.Count)
            throw new FormatException($"{section}存在重复或无效卡牌条目。");
    }

    private static void Write(Stream stream, int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        var remaining = (uint)value;
        while (remaining >= 128)
        {
            stream.WriteByte((byte)((remaining & 0x7f) | 0x80));
            remaining >>= 7;
        }
        stream.WriteByte((byte)remaining);
    }

    private sealed class DeckReader(byte[] bytes)
    {
        private int _offset;
        public bool AtEnd => _offset == bytes.Length;
        public int Read()
        {
            uint value = 0;
            for (var shift = 0; shift <= 28; shift += 7)
            {
                if (_offset >= bytes.Length) throw new FormatException("牌码在整数读取过程中被截断。");
                var current = bytes[_offset++];
                if (shift == 28 && (current & 0xf8) != 0) throw new FormatException("牌码整数溢出。");
                value |= (uint)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) return (int)value;
            }
            throw new FormatException("牌码整数超出支持长度。");
        }
        public int ReadPositive() { var value = Read(); return value > 0 ? value : throw new FormatException("牌码标识和数量必须为正数。"); }
        public int ReadCount() { var value = Read(); return value <= MaximumEntries ? value : throw new FormatException("牌码条目过多。"); }
    }
}
