using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using HsHelper.Core;

namespace HsHelper.Storage;

/// <summary>Snapshot serialization stores init properties and canonical frozen maps, excluding computed duplicate views.</summary>
public static class SnapshotCodec
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(), new FrozenMapConverterFactory() }
    };

    public static string Serialize(GameSnapshot snapshot)
    {
        if (snapshot.DeckTracking is { } tracking)
            snapshot = snapshot with { DeckTracking = tracking with
            {
                KnownRemaining = new SortedDictionary<string, int>(tracking.KnownRemaining.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal)
            } };
        return JsonSerializer.Serialize(snapshot, Options);
    }
    public static GameSnapshot Deserialize(string json)
    {
        var snapshot = JsonSerializer.Deserialize<GameSnapshot>(json, Options)
            ?? throw new InvalidDataException("保存的局面数据为空。");
        if (snapshot.Entities is null || snapshot.Quality is null || snapshot.Quality.Warnings is null || snapshot.RecentEvents is null ||
            snapshot.Entities.Values.Any(entity => entity is null || entity.Tags is null) ||
            snapshot.Choice is { Options: null } || snapshot.DeckTracking is { KnownRemaining: null } or { Warnings: null } ||
            snapshot.ChoiceHistory is { } history && (history.Entries.Any(entry => entry is null ||
                entry.Options.Any(option => option is null) || entry.OptionEntities.Any(entity => entity is null || entity.Tags is null))) ||
            snapshot.TurnHistory is { } turns && turns.Turns.Any(turn => turn is null ||
                turn.Plays.Any(play => play is null || play.AtPlay is null || play.AtPlay.Tags is null)))
            throw new InvalidDataException("保存的局面缺少必要字段。");
        return snapshot with
        {
            Quality = snapshot.Quality with { Warnings = Array.AsReadOnly(snapshot.Quality.Warnings.ToArray()) },
            Choice = snapshot.Choice is { } choice
                ? choice with { Options = Array.AsReadOnly(choice.Options.ToArray()) } : null,
            RecentEvents = Array.AsReadOnly(snapshot.RecentEvents.ToArray()),
            DeckTracking = snapshot.DeckTracking is { } tracking ? tracking with
            {
                KnownRemaining = tracking.KnownRemaining.ToFrozenDictionary(StringComparer.Ordinal),
                Warnings = Array.AsReadOnly(tracking.Warnings.ToArray())
            } : null
        };
    }

    private sealed class FrozenMapConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FrozenDictionary<,>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(FrozenMapConverter<,>).MakeGenericType(type.GetGenericArguments()))!;
    }

    private sealed class FrozenMapConverter<TKey, TValue> : JsonConverter<FrozenDictionary<TKey, TValue>> where TKey : notnull
    {
        public override FrozenDictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => (JsonSerializer.Deserialize<Dictionary<TKey, TValue>>(ref reader, options)
                ?? throw new JsonException("快照映射为空。" )).ToFrozenDictionary();
        public override void Write(Utf8JsonWriter writer, FrozenDictionary<TKey, TValue> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value), options);
    }
}
