using System;
using System.Collections.Generic;

/// <summary>
/// One faction's research web, put together from the catalogue:
///   • every node for all factions, plus that faction's own nodes;
///   • a node with Replaces takes the replaced node's place — its links, its
///     Start, and its position unless it has its own — and links to the
///     replaced id lead to it;
///   • links are made two-way, and links to nodes outside this faction's
///     web are dropped.
/// The importer builds every faction's web the same way to check it.
/// </summary>
public class ResearchWeb
{
    public class WebNode
    {
        public ResearchCatalogue.Node Data;
        public string Id => Data.id;
        /// <summary>The shared node this one stands in for, or null.</summary>
        public string ReplacesId;
        public bool   Start;
        public float  Angle, Radius;
        public readonly List<WebNode>        Links   = new();
        public readonly List<ResearchEffect> Effects = new();
        public UnlockRule Requires;

        public float Cost => Math.Max(0f, Data.cost);
    }

    public string ContentFactionId { get; }
    public readonly List<WebNode> Nodes = new();
    private readonly Dictionary<string, WebNode> _byId = new(StringComparer.OrdinalIgnoreCase);

    public WebNode Find(string id) => id != null && _byId.TryGetValue(id, out var n) ? n : null;

    private ResearchWeb(string contentFactionId) => ContentFactionId = contentFactionId;

    /// <summary>The web for one faction. Problems (bad links, effects, rules) go to warnings if given.</summary>
    public static ResearchWeb Build(ResearchCatalogue catalogue, string contentFactionId, List<string> warnings = null)
    {
        var web = new ResearchWeb(contentFactionId);
        if (catalogue == null) return web;

        var included = new List<ResearchCatalogue.Node>();
        foreach (var n in catalogue.nodes)
            if (n != null && !string.IsNullOrEmpty(n.id) && n.IsFor(contentFactionId)) included.Add(n);

        // Replacements first: which shared nodes are swapped out, and by what.
        var replacedBy = new Dictionary<string, ResearchCatalogue.Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in included)
        {
            if (string.IsNullOrEmpty(n.replaces)) continue;
            if (replacedBy.ContainsKey(n.replaces))
                warnings?.Add($"{contentFactionId}: {n.id} and {replacedBy[n.replaces].id} both replace {n.replaces} — {replacedBy[n.replaces].id} kept.");
            else replacedBy[n.replaces] = n;
        }

        var original = new Dictionary<string, ResearchCatalogue.Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in included) original[n.id] = n;

        foreach (var n in included)
        {
            if (replacedBy.ContainsKey(n.id)) continue;   // swapped out for this faction
            var node = new WebNode { Data = n, Start = n.start, Angle = n.angle, Radius = n.radius };
            if (!string.IsNullOrEmpty(n.replaces))
            {
                if (original.TryGetValue(n.replaces, out var old))
                {
                    node.ReplacesId = old.id;
                    node.Start |= old.start;
                    if (!n.hasPosition) { node.Angle = old.angle; node.Radius = old.radius; }
                }
                else warnings?.Add($"{contentFactionId}: {n.id} replaces {n.replaces}, which isn't in this faction's web.");
            }
            web.Nodes.Add(node);
            web._byId[n.id] = node;
        }
        foreach (var pair in replacedBy)
            if (web._byId.TryGetValue(pair.Value.id, out var node) && original.ContainsKey(pair.Key))
                web._byId[pair.Key] = node;

        // Links, both ways — a replaced node's links carry over to its replacement.
        void Link(WebNode a, string otherId, string owner)
        {
            var b = web.Find(otherId);
            if (b == null)
            {
                if (catalogue.Find(otherId) == null) warnings?.Add($"{owner} links to {otherId}, which isn't a node.");
                return;   // a node for other factions only — not in this web
            }
            if (b == a) return;
            if (!a.Links.Contains(b)) a.Links.Add(b);
            if (!b.Links.Contains(a)) b.Links.Add(a);
        }
        foreach (var node in web.Nodes)
        {
            foreach (var id in node.Data.links) Link(node, id, node.Id);
            if (node.ReplacesId != null)
                foreach (var id in original[node.ReplacesId].links) Link(node, id, node.ReplacesId);
        }
        // Nodes that list a replaced node as a link already resolve to the replacement via _byId.

        foreach (var node in web.Nodes)
        {
            foreach (var text in node.Data.effects)
            {
                var effect = ResearchEffect.Parse(text, out string error);
                if (effect != null) node.Effects.Add(effect);
                else warnings?.Add($"{node.Id}: effect '{text}' — {error}.");
            }
            node.Requires = UnlockRule.Parse(node.Data.requires);
            if (node.Requires.Error != null) warnings?.Add($"{node.Id}: Requires '{node.Data.requires}' — {node.Requires.Error}.");
        }
        return web;
    }

    /// <summary>Everything some node in this web unlocks — locked until that node is researched.</summary>
    public IEnumerable<string> UnlockKeys()
    {
        foreach (var n in Nodes)
            foreach (var e in n.Effects)
                if (e.isUnlock) yield return e.UnlockKey;
    }

    /// <summary>The node that unlocks something, or null.</summary>
    public WebNode Unlocking(UnlockKind kind, string id)
    {
        string key = ResearchEffect.Key(kind, id);
        foreach (var n in Nodes)
            foreach (var e in n.Effects)
                if (e.isUnlock && e.UnlockKey == key) return n;
        return null;
    }

    /// <summary>
    /// The nodes still to research to reach target, nearest first and target
    /// last: the fewest nodes from anything done (or a Start node). Empty if
    /// the target is done; null if it can't be reached.
    /// </summary>
    public List<WebNode> RouteTo(WebNode target, Func<WebNode, bool> done)
    {
        if (target == null) return null;
        if (done(target)) return new List<WebNode>();

        var from  = new Dictionary<WebNode, WebNode>();
        var queue = new Queue<WebNode>();
        // Seeds: open nodes — Start nodes, or nodes next to something done.
        foreach (var n in Nodes)
        {
            if (done(n)) continue;
            bool open = n.Start;
            if (!open) foreach (var l in n.Links) if (done(l)) { open = true; break; }
            if (!open) continue;
            from[n] = null;
            queue.Enqueue(n);
        }
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n == target)
            {
                var route = new List<WebNode>();
                for (var at = n; at != null; at = from[at]) route.Add(at);
                route.Reverse();
                return route;
            }
            foreach (var l in n.Links)
            {
                if (done(l) || from.ContainsKey(l)) continue;
                from[l] = n;
                queue.Enqueue(l);
            }
        }
        return null;
    }

    /// <summary>Nodes no Start node leads to — they could never be researched.</summary>
    public List<WebNode> Unreachable()
    {
        var seen  = new HashSet<WebNode>();
        var queue = new Queue<WebNode>();
        foreach (var n in Nodes) if (n.Start && seen.Add(n)) queue.Enqueue(n);
        while (queue.Count > 0)
            foreach (var l in queue.Dequeue().Links)
                if (seen.Add(l)) queue.Enqueue(l);
        var result = new List<WebNode>();
        foreach (var n in Nodes) if (!seen.Contains(n)) result.Add(n);
        return result;
    }
}
