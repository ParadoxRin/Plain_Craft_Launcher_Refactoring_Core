namespace Pcl.Domain;
/// <summary>A discovered archive identity, not merely a mod ID/version. Parents identify enclosing archives.</summary>
public sealed record FabricCandidate(
    string Key,
    string Id,
    string Version,
    IReadOnlyList<string> Provides,
    IReadOnlyList<FabricDependency> Dependencies,
    IReadOnlyList<string> Parents,
    string Origin,
    bool Required,
    string Environment = "*",
    int Schema = 1,
    int Depth = 0,
    bool IsBuiltin = false
);
public sealed record FabricConstraint(
    string Source,
    string Origin,
    string Relation,
    string Target,
    string Range,
    IReadOnlyList<string> Candidates
);
public sealed record FabricSolveLimits(
    int Candidates = 2048,
    int Clauses = 100000,
    int SearchNodes = 20000,
    long LiteralChecks = 5000000
);
public sealed record FabricResolution(
    string Status,
    IReadOnlyList<FabricCandidate> Selected,
    IReadOnlyList<FabricConstraint> Conflicts,
    IReadOnlyList<string> Warnings,
    int SearchNodes
);
/// <summary>Preserves structured, bounded evidence across the IO adapter; no filesystem paths or tokens are needed.</summary>
public sealed class FabricResolutionException(FabricResolution resolution) : Exception("Fabric dependency resolution: " + resolution.Status)
{
    public FabricResolution Resolution { get; } = resolution;
}

/// <summary>Bounded Boolean constraint search. Roots are pinned; nested candidates are optional, with parent support.</summary>
public static class FabricCandidateSolver
{
    private sealed record Clause(int[] Literals, FabricConstraint Evidence);
    private sealed record State(sbyte[] Values, int[] Reasons);
    private sealed class BudgetExceeded : Exception;
    public static FabricResolution Resolve(
        IReadOnlyList<FabricCandidate> input,
        CancellationToken ct = default,
        FabricSolveLimits? limits = null
    )
    {
        limits ??= new();
        var clauses = new List<Clause>();
        var evidence = new List<FabricConstraint>();
        var nodes = 0;
        long checks = 0;
        var uniqueEvidence = new HashSet<FabricConstraint>();
        void Record(FabricConstraint item)
        {
            if (evidence.Count < 48 && uniqueEvidence.Add(item))
            {
                evidence.Add(item);
            }
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (input.Count > limits.Candidates)
            {
                throw new BudgetExceeded();
            }

            if (input.Select(
                x => x.Key
            )
                .Distinct(StringComparer.Ordinal)
                .Count() != input.Count)
            {
                throw new ArgumentException("Candidate archive identities must be unique.");
            }

            // Server-only candidates are not loaded. Schema 0/1's environment compatibility relaxation is explicit below.
            var disabled = input.Where(
                x => x.Environment == "server"
            )
                .ToArray();
            var mods = input.Where(
                x => x.Environment != "server"
            )
                .OrderByDescending(
                    x => x.Required
                )
                .ThenBy(
                    x => x.Id,
                    StringComparer.Ordinal
                )
                .ThenByDescending(
                    x => x.Version,
                    Comparer<string>.Create(FabricVersions.Compare)
                )
                .ThenBy(
                    x => x.Depth
                )
                .ThenBy(
                    x => x.Key,
                    StringComparer.Ordinal
                )
                .ToArray();
            var byKey = mods.Select(
                (m, i) => (m.Key, i)
            )
                .ToDictionary(
                    x => x.Key,
                    x => x.i,
                    StringComparer.Ordinal
                );
            var byId = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < mods.Length; i++)
            {
                foreach (var id in mods[i].Provides.Prepend(mods[i].Id)
                    .Distinct(StringComparer.Ordinal))
                {
                    if (!byId.TryGetValue(id, out var list))
                    {
                        byId[id] = list = new();
                    }

                    list.Add(i);
                }
            }

            string Label(int index) => mods[index].Id + "@" + mods[index].Version;
            void Add(IEnumerable<int> terms, FabricConstraint reason)
            {
                var literals = terms.Distinct()
                    .ToArray();
                if (literals.Any(
                    x => literals.Contains(-x)
                ))
                {
                    return; // Self-provided dependency: a tautology, not a missing edge.
                }

                if (clauses.Count >= limits.Clauses)
                {
                    throw new BudgetExceeded();
                }

                clauses.Add(
                    new(literals, reason)
                );
            }

            foreach (var pair in byId)
            {
                if (pair.Value.Count > 1 && pair.Value.Any(
                    index => mods[index].IsBuiltin
                ))
                {
                    var builtin = pair.Value.First(
                        index => mods[index].IsBuiltin
                    );
                    return new(
                        "unsatisfiable",
                        [],
                        [
                            new(
                                Label(builtin),
                                mods[builtin].Origin,
                                "unique",
                                pair.Key,
                                "builtin ID is reserved",
                                pair.Value.Where(
                                    index => index != builtin
                                )
                                    .Select(
                                        index => Label(index) + " · " + mods[index].Origin
                                    )
                                    .ToArray()
                            )
                        ],
                        [],
                        nodes
                    );
                }

                if (pair.Value.Count > 64)
                {
                    throw new BudgetExceeded();
                }

                for (var a = 0; a < pair.Value.Count; a++)
                {
                    for (var b = a + 1; b < pair.Value.Count; b++)
                    {
                        var left = pair.Value[a];
                        var right = pair.Value[b];
                        Add(
                            [
                                -(left + 1),
                                -(right + 1)
                            ],
                            new(
                                Label(left),
                                mods[left].Origin,
                                "unique",
                                pair.Key,
                                "one provider",
                                [
                                    Label(right) + " · " + mods[right].Origin
                                ]
                            )
                        );
                    }
                }
            }

            for (var i = 0; i < mods.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var mod = mods[i];
                if (mod.Required)
                {
                    Add(
                        [
                            i + 1
                        ],
                        new(
                            Label(i),
                            mod.Origin,
                            "locked",
                            mod.Id,
                            mod.Version,
                            []
                        )
                    );
                }
                else
                {
                    var parents = mod.Parents.Where(byKey.ContainsKey)
                        .Select(
                            x => byKey[x]
                        )
                        .ToArray();
                    Add(
                        parents.Select(
                            x => x + 1
                        )
                            .Prepend(-(i + 1)),
                        new(
                            Label(i),
                            mod.Origin,
                            "parent",
                            "enclosing archive",
                            "selected",
                            parents.Select(Label)
                                .ToArray()
                        )
                    );
                }

                foreach (var dependency in mod.Dependencies.Where(
                    x => x.Kind is "depends" or "breaks"
                ))
                {
                    var available = byId.GetValueOrDefault(dependency.Id) ?? [];
                    if (dependency.Kind == "depends" && mod.Schema <= 1 && available.Count == 0 && disabled.Any(
                        x => x.Id == dependency.Id && dependency.Matchers.Any(
                            p => FabricVersions.Matches(x.Version, p)
                        )
                    ))
                    {
                        continue; // Match the Loader's compatibility relaxation for environment-disabled modules only.
                    }

                    var matching = available.Where(
                        x => dependency.Matchers.Any(
                            p => FabricVersions.Matches(mods[x].Version, p)
                        )
                    )
                        .ToArray();
                    var reason = new FabricConstraint(
                        Label(i),
                        mod.Origin,
                        dependency.Kind,
                        dependency.Id,
                        string.Join(" OR ", dependency.Matchers),
                        available.Take(12)
                            .Select(
                                x => Label(x) + " · " + mods[x].Origin
                            )
                            .ToArray()
                    );
                    if (dependency.Kind == "depends")
                    {
                        Add(
                            matching.Select(
                                x => x + 1
                            )
                                .Prepend(-(i + 1)),
                            reason
                        );
                    }
                    else
                    {
                        foreach (var other in matching)
                        {
                            Add(
                                [
                                    -(i + 1),
                                    -(other + 1)
                                ],
                                reason
                            );
                        }
                    }
                }
            }

            // Trace the propagation reasons behind a contradiction, including the pinned roots that caused it.
            // This is a bounded explanation, not a promise of a minimum unsatisfiable core.
            void Explain(int conflict, State state)
            {
                var pending = new Stack<int>();
                var seen = new HashSet<int>();
                pending.Push(conflict);
                while (pending.TryPop(out var index) && seen.Count < 256)
                {
                    if (!seen.Add(index))
                    {
                        continue;
                    }

                    Record(clauses[index].Evidence);
                    foreach (var literal in clauses[index].Literals)
                    {
                        var cause = state.Reasons[Math.Abs(literal) - 1];
                        if (cause >= 0 && cause != index)
                        {
                            pending.Push(cause);
                        }
                    }
                }
            }

            bool Propagate(State state)
            {
                var changed = true;
                while (changed)
                {
                    ct.ThrowIfCancellationRequested();
                    changed = false;
                    for (var c = 0; c < clauses.Count; c++)
                    {
                        var unknown = 0;
                        var remaining = 0;
                        var satisfied = false;
                        foreach (var literal in clauses[c].Literals)
                        {
                            if (++checks > limits.LiteralChecks)
                            {
                                throw new BudgetExceeded();
                            }

                            var value = state.Values[Math.Abs(literal) - 1];
                            if (value == 0)
                            {
                                unknown++;
                                remaining = literal;
                            }
                            else if (value == Math.Sign(literal))
                            {
                                satisfied = true;
                                break;
                            }
                        }

                        if (satisfied)
                        {
                            continue;
                        }

                        if (unknown == 0)
                        {
                            Explain(c, state);
                            return false;
                        }

                        if (unknown == 1)
                        {
                            var variable = Math.Abs(remaining) - 1;
                            state.Values[variable] = (sbyte)Math.Sign(remaining);
                            state.Reasons[variable] = c;
                            changed = true;
                        }
                    }
                }

                return true;
            }

            var stack = new Stack<State>();
            stack.Push(
                new(
                    new sbyte[mods.Length],
                    Enumerable.Repeat(-1, mods.Length)
                        .ToArray()
                )
            );
            while (stack.TryPop(out var state))
            {
                ct.ThrowIfCancellationRequested();
                if (++nodes > limits.SearchNodes)
                {
                    throw new BudgetExceeded();
                }

                if (!Propagate(state))
                {
                    continue;
                }

                var next = Array.IndexOf(state.Values, (sbyte)0);
                if (next < 0)
                {
                    var selected = mods.Where(
                        (_, i) => state.Values[i] == 1
                    )
                        .ToArray();
                    var warnings = new List<string>();
                    foreach (var mod in selected)
                    {
                        foreach (var soft in mod.Dependencies.Where(
                            x => x.Kind is "recommends" or "conflicts"
                        ))
                        {
                            var present = selected.Any(
                                x => (x.Id == soft.Id || x.Provides.Contains(soft.Id)) && soft.Matchers.Any(
                                    p => FabricVersions.Matches(x.Version, p)
                                )
                            );
                            if (soft.Kind == "conflicts"
                                ? present
                                : !present)
                            {
                                warnings.Add(
                                    mod.Id + ": " + soft.Kind + " " + soft.Id + " " + string.Join(" OR ", soft.Matchers)
                                );
                            }
                        }
                    }

                    return new(
                        "satisfied",
                        selected,
                        [],
                        warnings,
                        nodes
                    );
                }

                // Try preferred candidates first, but keep the alternate branch. An incompatible highest version cannot hide an older valid one.
                var alternative = new State(
                    (sbyte[])state.Values.Clone(),
                    (int[])state.Reasons.Clone()
                );
                alternative.Values[next] = -1;
                stack.Push(alternative);
                state.Values[next] = 1;
                stack.Push(state);
            }

            return new(
                "unsatisfiable",
                [],
                evidence,
                [],
                nodes
            );
        }
        catch (BudgetExceeded)
        {
            return new(
                "limit",
                [],
                [],
                [
                    "Dependency search limit reached; compatibility is undetermined."
                ],
                nodes
            );
        }
    }
}
