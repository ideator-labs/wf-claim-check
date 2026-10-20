#!/usr/bin/env bash
# Proves: on a real Workflow Core host with SQLite persistence, WfClaimCheck keeps a
# 3 MB workflow payload out of the Workflow row, stores it in a payload store, and the
# workflow still finishes in a new process with every byte intact.
#   bash demo.sh           all checks must PASS, exit 0
#   bash demo.sh --break   the provider stops swapping data for a ticket, a check must FAIL, exit 1
set -euo pipefail
cd "$(dirname "$0")"
[[ -d "$HOME/.dotnet" ]] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v jq >/dev/null || { echo "jq is required"; exit 2; }

OUT="$PWD/.demo"
SRC=src
rm -rf "$OUT" && mkdir -p "$OUT"

sabotage() {  # sabotage <file> <exact text to remove>
  grep -qF -- "$2" "$1" || { echo "sabotage target not found in $1"; exit 2; }
  F="$1" T="$2" python3 -c 'import os; p=os.environ["F"]; s=open(p).read(); open(p,"w").write(s.replace(os.environ["T"], "", 1))'
}

case "${1:-}" in
  "") ;;
  --break)
    cp -r src "$OUT/src" && rm -rf "$OUT"/src/*/bin "$OUT"/src/*/obj && SRC="$OUT/src"
    sabotage "$SRC/WfClaimCheck/ClaimCheckPersistenceProvider.cs" "workflow.Data = ticket;"
    echo "BREAK MODE: the provider writes the payload but leaves the data inline. Expect a FAIL." ;;
  *) echo "usage: bash demo.sh [--break]"; exit 2 ;;
esac

FAILED=0
pass() { echo "CHECK PASS  $1"; }
fail() { echo "CHECK FAIL  $1"; FAILED=1; }

if dotnet build "$SRC/WfClaimCheck.Demo" -c Release -o "$OUT/bin" -v quiet -nologo > "$OUT/build.log" 2>&1; then
  pass "1 build: WfClaimCheck + demo compile against WorkflowCore 3.21.0 and WorkflowCore.Persistence.Sqlite 3.21.0"
else
  cat "$OUT/build.log"; fail "1 build"; echo "RESULT: FAIL"; exit 1
fi

APP="dotnet $OUT/bin/WfClaimCheck.Demo.dll"
BLOBS="$OUT/blobs"
row() { $APP inspect "$1" | jq -c --arg id "${2^^}" '.[] | select(.id == $id)'; }

$APP baseline "$OUT/baseline.db" > "$OUT/baseline.json" 2> "$OUT/baseline.log"
base_id="$(jq -r .large "$OUT/baseline.json")"
base_bytes="$(row "$OUT/baseline.db" "$base_id" | jq .dataBytes)"
if [[ "$base_bytes" -ge 3000000 ]]; then
  pass "2 control: plain UseSqlite stores the whole payload in the Workflow row ($base_bytes bytes)"
else
  fail "2 control: expected >= 3000000 bytes in the plain row, got $base_bytes"
fi

$APP start "$OUT/claim.db" "$BLOBS" "$OUT/state.json" > "$OUT/start.json" 2> "$OUT/start.log"
large_id="$(jq -r .large "$OUT/state.json")"
small_id="$(jq -r .small "$OUT/state.json")"
large_row="$(row "$OUT/claim.db" "$large_id")"
large_bytes="$(echo "$large_row" | jq .dataBytes)"
ticket="$(echo "$large_row" | jq -r '.data // "{}"' | jq -c 'select(."$type" == "WfClaimCheck.ClaimTicket, WfClaimCheck")')"
if [[ "$large_bytes" -lt 1024 && -n "$ticket" ]]; then
  pass "3 offload: with UseClaimCheck the waiting row holds a ClaimTicket ($large_bytes bytes)"
else
  fail "3 offload: expected a ClaimTicket under 1024 bytes, got $large_bytes bytes"
fi

blob_files=("$BLOBS"/*.json)
key="$(echo "${ticket:-{\}}" | jq -r '.Key // empty')"
want_sha="$(echo "${ticket:-{\}}" | jq -r '.Sha256 // empty')"
if [[ ${#blob_files[@]} -eq 1 && -n "$key" && -f "$BLOBS/$key.json" ]] \
   && [[ "$(stat -c %s "$BLOBS/$key.json")" -ge 3000000 ]] \
   && [[ "$(sha256sum "$BLOBS/$key.json" | cut -d' ' -f1)" == "$want_sha" ]]; then
  pass "4 store: one payload file, >= 3000000 bytes, sha256sum matches the ticket"
else
  fail "4 store: expected one payload file named by the ticket with a matching sha256 (files: ${#blob_files[@]}, key: ${key:-none})"
fi

small_row="$(row "$OUT/claim.db" "$small_id")"
if [[ "$(echo "$small_row" | jq -r .data)" == *'"RunKey":"small"'* ]]; then
  pass "5 threshold: the small run stays inline ($(echo "$small_row" | jq .dataBytes) bytes) and adds no payload file"
else
  fail "5 threshold: small run data is not inline"
fi

start_key="$key"
$APP resume "$OUT/claim.db" "$BLOBS" "$OUT/state.json" > "$OUT/resume.json" 2> "$OUT/resume.log"
if [[ "$(jq -r '.large.status + "/" + .small.status' "$OUT/resume.json")" == "Complete/Complete" ]]; then
  pass "6 restart: a new process publishes the approvals and both runs reach Complete"
else
  fail "6 restart: statuses $(jq -c '[.large.status, .small.status]' "$OUT/resume.json")"
fi

if jq -e '.large.SeenSha == .large.SourceSha and .large.SeenChars == 3000000' "$OUT/resume.json" > /dev/null; then
  pass "7 integrity: the Summarize step in process 2 saw all 3000000 chars with the SHA-256 taken in process 1"
else
  fail "7 integrity: Summarize saw $(jq -c '{SeenChars: .large.SeenChars, same: (.large.SeenSha == .large.SourceSha)}' "$OUT/resume.json")"
fi

final_key="$(row "$OUT/claim.db" "$large_id" | jq -r '.data // "{}"' | jq -r '.Key // empty')"
blob_files=("$BLOBS"/*.json)
if [[ ${#blob_files[@]} -eq 1 && -n "$final_key" && "$final_key" != "$start_key" && -f "$BLOBS/$final_key.json" ]]; then
  pass "8 cleanup: Summarize changed the data, a new payload replaced the old one, one file remains"
else
  fail "8 cleanup: expected exactly one payload file for a new ticket (files: ${#blob_files[@]}, start: ${start_key:-none}, final: ${final_key:-none})"
fi

mkdir -p "$OUT/tampered" && cp "$OUT/claim.db" "$OUT/tampered/" && cp -r "$BLOBS" "$OUT/tampered/blobs"
if [[ -n "$final_key" ]]; then
  printf 'X' | dd of="$OUT/tampered/blobs/$final_key.json" bs=1 seek=100 conv=notrunc status=none
fi
set +e
tamper_out="$($APP load "$OUT/tampered/claim.db" "$OUT/tampered/blobs" "$large_id" 2>&1)"; tamper_rc=$?
set -e
if [[ $tamper_rc -eq 3 && "$tamper_out" == refused:* ]]; then
  pass "9 tamper: a payload edited after the fact is refused on load (SHA-256 mismatch)"
else
  fail "9 tamper: expected a refusal, got rc=$tamper_rc: ${tamper_out:0:120}"
fi

echo "--- rows ---"
$APP inspect "$OUT/claim.db" | jq -c '.[] | {id: .id[0:8], status, dataBytes}'
echo "--- payload files ---"
ls -l "$BLOBS"
echo "--- resume report ---"
jq -c '.large | {status, SeenChars, Summary}' "$OUT/resume.json"

if [[ $FAILED -ne 0 ]]; then echo "RESULT: FAIL (raw output in $OUT/)"; exit 1; fi
echo "RESULT: PASS"
