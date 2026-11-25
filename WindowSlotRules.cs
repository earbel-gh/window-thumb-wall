namespace WindowThumbWall;

internal sealed class WindowSlotRules
{
    private readonly Dictionary<Guid, WindowRuleState> _rules = [];

    internal IEnumerable<WindowRuleState> All => _rules.Values;

    internal void Restore(IEnumerable<WindowRuleState> rules)
    {
        foreach (WindowRuleState rule in rules)
            _rules.Add(rule.RuleId, rule);
    }

    internal static SlotState CreateIntent(
        Guid slotId, string processName, string title, WindowIdentity identity,
        bool isAutomatic, Guid? ruleId = null) => new()
    {
        SlotId = slotId,
        RuleId = ruleId,
        IsPinned = ruleId != null,
        ProcessName = processName,
        Title = title,
        Identity = identity,
        IsAutomatic = isAutomatic
    };

    internal void SetRule(SlotState intent)
    {
        Guid ruleId = intent.RuleId ?? Guid.NewGuid();
        _rules[ruleId] = new WindowRuleState
        {
            RuleId = ruleId,
            ProcessName = intent.ProcessName,
            TitleAnchor = intent.Title,
            Identity = intent.Identity
        };
        intent.RuleId = ruleId;
        intent.IsPinned = true;
        intent.IsAutomatic = false;
    }

    internal bool RemoveRule(Guid ruleId, SlotState? intent)
    {
        if (!_rules.Remove(ruleId))
            return false;
        if (intent?.RuleId == ruleId)
        {
            intent.RuleId = null;
            intent.IsPinned = false;
        }
        return true;
    }

    internal SlotState GetMatchState(SlotState intent)
    {
        if (intent.RuleId is not Guid ruleId || !_rules.TryGetValue(ruleId, out WindowRuleState? rule))
            return intent;

        return new SlotState
        {
            SlotId = intent.SlotId,
            RuleId = rule.RuleId,
            IsPinned = true,
            ProcessName = rule.ProcessName,
            Title = rule.TitleAnchor,
            Identity = rule.Identity?.WithoutHandle(),
            LastBindingIdentity = intent.LastBindingIdentity
        };
    }

    internal static SlotState CreatePlaceholder(WindowRuleState rule) => new()
    {
        SlotId = Guid.NewGuid(),
        RuleId = rule.RuleId,
        IsPinned = true,
        ProcessName = rule.ProcessName,
        Title = rule.TitleAnchor,
        Identity = rule.Identity?.WithoutHandle()
    };

    internal static bool IsLegacyAppMatch(SlotState intent, WindowInfo window) =>
        intent.RuleId == null &&
        intent.Identity == null &&
        !string.IsNullOrWhiteSpace(intent.Title) &&
        string.Equals(intent.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(intent.Title.Trim(), window.Title.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static bool HasConflictingLegacyLifetime(SlotState intent, WindowInfo window) =>
        intent.LastBindingIdentity is { } previous &&
        window.Identity is { } current &&
        previous.Handle != window.Handle.ToInt64() &&
        WindowRestoreMatcher.IsSameProcessInstance(previous, current);
}
