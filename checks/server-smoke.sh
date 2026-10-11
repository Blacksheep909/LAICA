#!/usr/bin/env bash
# Starts laica-server and checks what a browser (or the Electron window) depends on:
# the interface is served, other sites and rebinding hosts are refused, login works, many open event streams do not
# stop commands from answering, and a write cannot follow a symbolic link out of the project.
# Usage: checks/server-smoke.sh <laica-server.dll> <ui folder>
set -u
DLL="$1"; UI="$2"; PORT="${PORT:-8931}"; PASS=smoke-pass-1234
WORK="$(mktemp -d)"; DATA="$(mktemp -d)"; trap 'kill $PID 2>/dev/null; rm -rf "$WORK" "$DATA"' EXIT
mkdir -p "$WORK/proj" "$WORK/outside"; ln -s "$WORK/outside" "$WORK/proj/escape"; echo old > "$WORK/outside/f.txt"; echo a > "$WORK/proj/a.txt"
LAICA_APP_PASSWORD=$PASS dotnet "$DLL" --ui "$UI" --data "$DATA" --port "$PORT" < <(tail -f /dev/null) > "$DATA/out.log" 2>&1 &
PID=$!
for i in $(seq 1 40); do grep -q READY "$DATA/out.log" 2>/dev/null && break; sleep 0.5; done
B=http://127.0.0.1:$PORT; fail=0
check(){ if [ "$2" = "$3" ]; then echo "PASS $1"; else echo "FAIL $1 (wanted $3, got $2)"; fail=1; fi; }
check "serves the interface" "$(curl -s -o /dev/null -w '%{http_code}' $B/)" 200
check "refuses a rebinding host" "$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: evil.example:80' $B/)" 421
check "refuses a cross-site login" "$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Origin: http://evil.example' -d '{"password":"x"}' $B/api/login)" 403
check "rejects a wrong password" "$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Origin: $B" -d '{"password":"nope"}' $B/api/login)" 401
check "needs a token for commands" "$(curl -s -o /dev/null -w '%{http_code}' -X POST -d '{"Method":"harnessList"}' $B/api/rpc)" 401
T=$(curl -s -X POST -d "{\"password\":\"$PASS\"}" $B/api/login | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[ -n "$T" ] && echo "PASS logs in" || { echo "FAIL logs in"; fail=1; }
for i in $(seq 1 40); do curl -s -N -H "Authorization: Bearer $T" $B/api/events -o /dev/null --max-time 8 & done; sleep 2
check "answers commands with 40 event streams open" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 -X POST -H "Authorization: Bearer $T" -d '{"Method":"harnessList","Payload":{}}' $B/api/rpc)" 200
rpc(){ curl -s -X POST -H "Authorization: Bearer $T" -d "$1" $B/api/rpc; }
ID=$(rpc "{\"Method\":\"harnessCreate\",\"Payload\":{\"Harness\":\"codex\",\"Cwd\":\"$WORK/proj\",\"Title\":\"t\"}}" | sed -n 's/.*"Id":"\([^"]*\)".*/\1/p')
w(){ rpc "{\"Method\":\"harnessWriteFile\",\"Payload\":{\"Id\":\"$ID\",\"Path\":\"$1\",\"Text\":\"new\"}}"; }
case "$(w a.txt)" in *'"Result":true'*) echo "PASS writes inside the project";; *) echo "FAIL writes inside the project"; fail=1;; esac
case "$(w escape/f.txt)" in *Error*) echo "PASS refuses to write through a link";; *) echo "FAIL refuses to write through a link"; fail=1;; esac
case "$(w ../outside/f.txt)" in *Error*) echo "PASS refuses ../ out of the project";; *) echo "FAIL refuses ../ out of the project"; fail=1;; esac
[ "$(cat "$WORK/outside/f.txt")" = old ] && echo "PASS the file outside is untouched" || { echo "FAIL the file outside is untouched"; fail=1; }
case "$(rpc '{"Method":"awakeGet","Payload":{}}')" in *'"Active":false'*) echo "PASS reports keep-awake state";; *) echo "FAIL reports keep-awake state"; fail=1;; esac
ls "$DATA"/logs/laica-*.log >/dev/null 2>&1 && echo "PASS writes a log file" || { echo "FAIL writes a log file"; fail=1; }
exit $fail
