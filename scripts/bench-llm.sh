#!/usr/bin/env bash
# Measures what a locally served model can actually do on this machine, against LM Studio's server.
# Run it on the machine hosting the model, once per model:
#
#   scripts/bench-llm.sh <model-identifier> [base-url]
#
# <model-identifier> is whatever the model was loaded as (`lms load ... --identifier bench` makes it
# "bench"). Needs curl and jq. Prints one line per test; paste the output back as-is.
#
# The four numbers that decide the design:
#   prefill  - how fast the model reads a prompt. On a CPU this is the bottleneck, because every agent
#              turn starts by reading SELF.md and the tool list.
#   cached   - the same prompt again. If the server reuses the prefix, this collapses, and a turn
#              that only adds a short message is cheap.
#   gen      - how fast it writes.
#   tool     - whether it calls request_capability correctly when it should. A model that can't is
#              no use as the agent, whatever its speed.
set -euo pipefail

MODEL=${1:?usage: bench-llm.sh <model-identifier> [base-url]}
BASE=${2:-http://localhost:1234}
SELF_MD="$(cd "$(dirname "$0")/.." && pwd)/Rampant.Seed/SELF.md"

for cmd in curl jq; do
    command -v "$cmd" >/dev/null || { echo "missing: $cmd (sudo apt install $cmd)"; exit 1; }
done
[ -f "$SELF_MD" ] || { echo "missing: $SELF_MD (run from a clone of the repo)"; exit 1; }

# A nonce up front so the first request can't be served from a prefix cached by an earlier run.
SYSTEM="Run $(date +%s%N).

$(cat "$SELF_MD")"

body() { # system, user, max_tokens
    jq -n --arg m "$MODEL" --arg s "$1" --arg u "$2" --argjson n "$3" \
        '{model: $m, messages: [{role: "system", content: $s}, {role: "user", content: $u}],
          max_tokens: $n, temperature: 0.7, stream: false}'
}

# LM Studio's native endpoint, not /v1, because it reports time-to-first-token. Wall time is printed
# too, so the line still means something if a server version stops returning stats.
timed() { # label, endpoint, json body
    local start end response
    start=$(date +%s.%N)
    response=$(curl -sS --max-time 1800 "$BASE$2" -H 'Content-Type: application/json' -d "$3")
    end=$(date +%s.%N)
    jq -r --arg l "$1" --arg start "$start" --arg end "$end" '
        if .error then "\($l): ERROR \(.error | tostring)" else
        (.usage.prompt_tokens // 0) as $p | (.stats.time_to_first_token // 0) as $t |
        "\($l): wall=\(($end | tonumber) - ($start | tonumber) | . * 10 | floor / 10)s"
        + " prompt=\($p) completion=\(.usage.completion_tokens // "?") stop=\(.choices[0].finish_reason // "?")"
        + if .stats.time_to_first_token then
            " ttft=\($t)s prefill=\(if $t > 0 then ($p / $t | floor | tostring) + " tok/s" else "?" end)"
            + " gen=\(.stats.tokens_per_second // 0 | floor) tok/s"
          else "" end
        end' <<<"$response"
    LAST=$response
}

echo "model=$MODEL server=$BASE host=$(hostname) $(date -u +%FT%TZ)"

COLD=$(body "$SYSTEM" "Reply with the single word OK." 16)
timed "prefill" /api/v0/chat/completions "$COLD"
timed "cached " /api/v0/chat/completions "$COLD"

timed "gen    " /api/v0/chat/completions "$(body "You are a careful C# programmer." \
    "Write a C# class that parses ISO-8601 durations like P3DT4H, with comments." 300)"

# The agent's only genesis tool, declared the way Microsoft.Extensions.AI declares it, on the
# OpenAI-compatible endpoint the agent will actually use.
TOOL_BODY=$(jq -n --arg m "$MODEL" --arg s "$SYSTEM" '{
    model: $m, max_tokens: 1024, temperature: 0.7, stream: false,
    messages: [{role: "system", content: $s},
               {role: "user", content: "Can you start reminding me to water the plants every Sunday morning?"}],
    tools: [{type: "function", function: {
        name: "request_capability",
        description: "Ask for a new capability to be built for you, when you have been asked to do something you have no tool for. Describe what you want in plain English; someone else writes the code.",
        parameters: {type: "object", required: ["capability", "description"], properties: {
            capability: {type: "string", description: "Short name for the capability, lowercase, e.g. reminders."},
            description: {type: "string", description: "What it should do, in plain English, specific enough to build without questions."}}}}}]}')
# Three samples, because one at this temperature says little about how reliably it calls; the system
# prompt is cached by now, so each costs only the new message and the reply.
for i in 1 2 3; do
    timed "tool $i " /v1/chat/completions "$TOOL_BODY"
    jq -r --arg l "tool $i " 'if .error then empty else .choices[0].message as $m |
        (if (($m.reasoning_content // "") | length) > 0 or (($m.content // "") | test("<think>"))
            then " (thought first)" else "" end) as $thought |
        if ($m.tool_calls | length) > 0 then
            "\($l): CALLED\($thought) \($m.tool_calls[0].function.name) \($m.tool_calls[0].function.arguments | .[0:300])"
        else "\($l): NO CALL\($thought), said: \($m.content // "" | .[0:300])" end end' <<<"$LAST"
done

command -v free >/dev/null && echo "memory: $(free -h | awk '/^Mem:/ {print "used=" $3 " available=" $7}')"
exit 0
