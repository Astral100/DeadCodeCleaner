namespace DeadCodeCleaner;

public static class Sweeper
{
    /// <summary>Mark-and-sweep: everything reachable from <paramref name="roots"/> through edges and
    /// rules, with <paramref name="removed"/> nodes treated as if deleted (never in use,
    /// never expanded).</summary>
    public static HashSet<object> Run(Graph graph, IEnumerable<string> roots, HashSet<string> removed)
    {
        var live = new HashSet<object>(NodeComparer.Instance);
        var queue = new Queue<object>();
        var ruleIndex = new Dictionary<object, List<Rule>>(NodeComparer.Instance);
        foreach (var rule in graph.Rules)
        {
            foreach (var a in rule.Antecedents)
            {
                if (!ruleIndex.TryGetValue(a, out var list)) { ruleIndex[a] = list = new List<Rule>(); }
                list.Add(rule);
            }
        }
        var satisfied = new Dictionary<Rule, int>();

        void Enqueue(object n)
        {
            if (n is string s && removed.Contains(s)) { return; }
            if (live.Add(n)) { queue.Enqueue(n); }
        }

        foreach (var r in roots) { Enqueue(r); }

        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n is string id)
            {
                if (graph.Edges.TryGetValue(id, out var targets))
                {
                    foreach (var t in targets) { Enqueue(t); }
                }
                if (graph.ContainerOf.TryGetValue(id, out var container)) { Enqueue(container); }
            }
            if (ruleIndex.TryGetValue(n, out var rules))
            {
                foreach (var rule in rules)
                {
                    satisfied.TryGetValue(rule, out var count);
                    satisfied[rule] = ++count;
                    if (count == rule.Antecedents.Length)
                    {
                        foreach (var c in rule.Consequents) { Enqueue(c); }
                    }
                }
            }
        }
        return live;
    }
}
