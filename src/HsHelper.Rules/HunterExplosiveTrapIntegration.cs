namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        public bool AuditedExplosiveTrapSource(Entity source)
        {
            if (source.CardId is { } id && ExplosiveTrapRules.IsAuditedSource(_cards.GetValueOrDefault(id)) &&
                _snapshot.Entities.TryGetValue(source.Id, out var observed) &&
                Consistent("SECRET", "219", value => value == "1") &&
                Consistent("SPELL_SCHOOL", "1635", value => value is "FIRE" or "2") &&
                Consistent("CARDRACE", "200", value => value is "INVALID" or "0")) return true;
            Unsupported("UNCONFIRMED_EXPLOSIVE_TRAP_DEFINITION", "爆炸陷阱的固定定义或当前奥秘、学派、种族存在未确认/冲突证据，未支付或安装。");
            return false;

            bool Consistent(string name, string alias, Func<string, bool> expected) =>
                (!observed!.Tags.TryGetValue(name, out var named) || expected(named)) &&
                (!observed.Tags.TryGetValue(alias, out var numeric) || expected(numeric));
        }
    }
}
