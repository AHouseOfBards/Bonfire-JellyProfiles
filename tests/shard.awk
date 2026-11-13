# Split harnesses into N balanced shards, longest first.
#
#   awk -v N=4 -f tests/shard.awk < items      # items: one "name cost" per line
#
# Prints "K name" for every item, K in 1..N. Greedy: take the items from the highest cost
# down (ties by name, so the result never depends on the order they arrive in) and give each
# to the shard that has the least so far (ties to the lowest K). Deterministic, and every
# item lands in exactly one shard. tests/run.sh --shard K/N uses it, and
# tests/js/shards.js proves the "exactly one" part for N = 1..8.
{ name[NR] = $1; cost[NR] = $2 + 0 }
END {
    n = NR
    for (i = 1; i <= n; i++) ord[i] = i
    # insertion sort, because mawk (ubuntu's awk) has no asort
    for (i = 2; i <= n; i++) {
        v = ord[i]; j = i - 1
        while (j >= 1 && (cost[ord[j]] < cost[v] || (cost[ord[j]] == cost[v] && name[ord[j]] > name[v]))) {
            ord[j + 1] = ord[j]; j--
        }
        ord[j + 1] = v
    }
    for (k = 1; k <= N; k++) load[k] = 0
    for (i = 1; i <= n; i++) {
        best = 1
        for (k = 2; k <= N; k++) if (load[k] < load[best]) best = k
        load[best] += cost[ord[i]]
        print best, name[ord[i]]
    }
}
