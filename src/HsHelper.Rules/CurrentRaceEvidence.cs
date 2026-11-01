using System.Globalization;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    // HearthDb 56a3a445ef8612816d71500bb5609c0114c35978 Enums.cs Race (fixed primary cache).
    // Named Power.log values and tag200 describe the same current value; neither may erase a contradiction.
    private static readonly IReadOnlyDictionary<string,int> CurrentRaces = new Dictionary<string,int>(StringComparer.Ordinal)
    {
        ["INVALID"]=0,["BLOODELF"]=1,["DRAENEI"]=2,["DWARF"]=3,["GNOME"]=4,["GOBLIN"]=5,["HUMAN"]=6,
        ["NIGHTELF"]=7,["ORC"]=8,["TAUREN"]=9,["TROLL"]=10,["UNDEAD"]=11,["WORGEN"]=12,["GOBLIN2"]=13,
        ["MURLOC"]=14,["DEMON"]=15,["SCOURGE"]=16,["MECHANICAL"]=17,["ELEMENTAL"]=18,["OGRE"]=19,
        ["BEAST"]=20,["PET"]=20,["TOTEM"]=21,["NERUBIAN"]=22,["PIRATE"]=23,["DRAGON"]=24,["BLANK"]=25,["ALL"]=26,
        ["EGG"]=38,["QUILBOAR"]=43,["CENTAUR"]=80,["FURBOLG"]=81,["HIGHELF"]=83,["TREANT"]=84,["OWLKIN"]=85,
        ["HALFORC"]=88,["LOCK"]=89,["NAGA"]=92,["OLDGOD"]=93,["PANDAREN"]=94,["GRONN"]=95,["CELESTIAL"]=96,
        ["GNOLL"]=97,["GOLEM"]=98,["HARPY"]=99,["VULPERA"]=100,["ABERRATION"]=126
    };
    private static readonly IReadOnlySet<int> CurrentRaceNumbers = CurrentRaces.Values.ToHashSet();

    private static bool TryObservedCurrentRace(EntitySnapshot observed, out int? race)
    {
        race=null;
        foreach(var name in new[]{"CARDRACE","200"})
        {
            if(!observed.Tags.TryGetValue(name,out var raw))continue;
            if(!CurrentRaces.TryGetValue(raw,out var parsed) &&
                (!int.TryParse(raw,NumberStyles.Integer,CultureInfo.InvariantCulture,out parsed) || !CurrentRaceNumbers.Contains(parsed))) return false;
            if(race.HasValue && race!=parsed)return false;
            race=parsed;
        }
        return true;
    }

    private sealed partial class Runtime
    {
        private static void NormalizeCurrentRace(Entity entity, EntitySnapshot observed)
        {
            if(TryObservedCurrentRace(observed,out var race) && race.HasValue)entity.Flags["CARDRACE"]=race.Value;
        }
        private bool IsKnownCurrentRaceNumeric(Entity entity,string tag) => tag=="200" &&
            _snapshot.Entities.TryGetValue(entity.Id,out var observed) && TryObservedCurrentRace(observed,out var race) && race.HasValue;
    }
}
