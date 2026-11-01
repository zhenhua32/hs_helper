namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    // Agamaggan's 10-cost ceiling and lethal/overkill exception were clarified by HearthstoneTeam:
    // https://www.reddit.com/r/hearthstone/comments/1j60agy/new_warlock_card_revealed_agamaggan/
    // Health payment is not damage and ignores Immune/Divine Shield:
    // https://hearthstone.blizzard.com/en-us/news/23934449
    private static readonly HashSet<string> OpponentHealthEnchantments = ["EDR_489e1", "EDR_489e2"];

    private sealed partial class Runtime
    {
        private bool? _nextCardCostsOpponentHealth = false;
        private readonly HashSet<int> _observedOpponentHealthEnchantments = [];
        private readonly List<OpponentHealthPayment> _opponentHealthPayments = [];

        public bool OpponentDefeatedByPayment { get; private set; }

        private void InitializeOpponentHealthPayment()
        {
            var candidates = Entities.Values.Where(e => e.Alive &&
                e.CardId is { } id && OpponentHealthEnchantments.Contains(id) &&
                e.Zone is "PLAY" or "SETASIDE" && e.Controller == Local).ToArray();
            var observed = candidates.Where(e => e.Type == "ENCHANTMENT" &&
                NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, e.CardId, _cards.GetValueOrDefault(e.CardId!))).ToArray();
            var active = observed.Where(e => e.Int("ATTACHED") is { } attached &&
                (attached == OwnHero?.Id || attached == Player?.Id) && Profile(e) is not null).ToArray();
            if (_snapshot.ClientBuild == AuditedBuild && observed.Length == 1 && active.Length == 1)
            {
                _nextCardCostsOpponentHealth = true;
                _observedOpponentHealthEnchantments.Add(active[0].Id);
                Coverage.Add("EDR_489-observed-player-or-hero-enchantment");
            }
            else if (candidates.Length > 0 || Entities.Values.Any(e => e.CardId == "EDR_489" &&
                e.Controller == Local && e.Zone is not ("HAND" or "DECK")))
            {
                // A battlecry survives its body's death or silence, but may already have been spent.
                // The body's presence alone cannot prove either active or consumed payment.
                _nextCardCostsOpponentHealth = null;
            }
        }

        public void ArmOpponentHealthPayment()
        {
            _nextCardCostsOpponentHealth = true;
            Coverage.Add("EDR_489-next-card-opponent-health-payment");
        }

        private bool IsObservedOpponentHealthEnchantment(Entity entity) =>
            _nextCardCostsOpponentHealth == true && _observedOpponentHealthEnchantments.Contains(entity.Id);

        public bool CanAffordCard(Entity source)
        {
            if (source.Cost is null) { Unsupported("UNKNOWN_CURRENT_COST", "手牌当前费用未知，不能使用基础印刷费用代替。"); return false; }
            if (source.Cost < 0) { Invalid("当前手牌费用无效。"); return false; }
            if (_nextCardCostsOpponentHealth is null)
            { Unsupported("UNKNOWN_AGAMAGGAN_PAYMENT", "已观察到阿迦玛甘或其附魔，但不能确认下一张牌的生命支付是否仍生效；请等待明确的当前附魔或客户端结算。"); return false; }
            if (_nextCardCostsOpponentHealth == true)
            {
                if (!RequireAuditedBuild()) return false;
                if (source.Cost > 10) { Invalid("阿迦玛甘生效时，当前费用超过10的牌不能使用，也不能用法力补差额。"); return false; }
                return true;
            }
            if (source.Cost > Mana) { Invalid("当前法力不足。"); return false; }
            return true;
        }

        private bool? PayWithOpponentHealth(Entity source)
        {
            if (source.Zone != "HAND" || _nextCardCostsOpponentHealth == false) return null;
            if (!CanAffordCard(source)) return false;
            if (EnemyHero?.Health is not { } before || before <= 0)
            { Unsupported("UNKNOWN_OPPONENT_HEALTH_PAYMENT", "对手当前英雄生命未知，不能结算生命支付。"); return false; }
            var cost = source.Cost!.Value;
            EnemyHero.Health = before - cost;
            _nextCardCostsOpponentHealth = false;
            foreach (var id in _observedOpponentHealthEnchantments)
            { Entities[id].Alive = false; Entities[id].Zone = "GRAVEYARD"; }
            _observedOpponentHealthEnchantments.Clear();
            _opponentHealthPayments.Add(new(source.Id, source.CardId!, EnemyHero.Id, cost, before, EnemyHero.Health.Value));
            OpponentDefeatedByPayment = EnemyHero.Health <= 0;
            if (OpponentDefeatedByPayment)
            { ResolveDeaths(); Coverage.Add("EDR_489-terminal-payment-card-resolution-stopped"); }
            Coverage.UnionWith(["EDR_489-current-cost-maximum-10", "EDR_489-health-payment-not-damage", "EDR_489-one-next-card"]);
            return true;
        }
    }
}
