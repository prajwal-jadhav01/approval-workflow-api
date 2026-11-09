#!/usr/bin/env bash
# End-to-end test against the real stack (API + Camunda 8 + Postgres), used by CI.
# Walks three claims through the BPMN process: small approved, large approved, cancelled.
set -euo pipefail
API=${API:-http://localhost:8080}

json() { python3 -c "import json,sys; print(json.load(sys.stdin)$1)"; }

token() {
  curl -sf -X POST "$API/dev/token" -H 'Content-Type: application/json' \
    -d "{\"userId\":\"$1\",\"roles\":[\"$2\"]}" | json "['accessToken']"
}

call() { # call TOKEN METHOD PATH [BODY]
  curl -sf -X "$2" "$API$3" -H "Authorization: Bearer $1" -H 'Content-Type: application/json' ${4:+-d "$4"}
}

wait_for_status() { # wait_for_status TOKEN CLAIM_ID EXPECTED
  for _ in $(seq 1 60); do
    status=$(call "$1" GET "/api/claims/$2" | json "['status']")
    [[ "$status" == "$3" ]] && { echo "  claim is $status"; return 0; }
    sleep 1
  done
  echo "FAIL: claim $2 is $status, expected $3"; exit 1
}

echo "Waiting for the API..."
for _ in $(seq 1 60); do curl -sf "$API/health" >/dev/null && break; sleep 2; done

EMP=$(token asha employee); MGR=$(token ravi manager); FIN=$(token meera finance)

new_claim() { # new_claim AMOUNT CATEGORY -> id
  call "$EMP" POST /api/claims \
    "{\"amount\":$1,\"currency\":\"INR\",\"category\":\"$2\",\"description\":\"Smoke test $2 $RANDOM\"}" | json "['id']"
}

echo "1) Small claim: manager approval only"
id=$(new_claim 800 meals)
call "$EMP" POST "/api/claims/$id/submit" >/dev/null
wait_for_status "$EMP" "$id" PendingManager
call "$MGR" POST "/api/claims/$id/manager-decision" '{"approved":true,"comment":"ok"}' >/dev/null
wait_for_status "$EMP" "$id" Approved

echo "2) Large claim: manager then finance"
id=$(new_claim 4500 hardware)
call "$EMP" POST "/api/claims/$id/submit" >/dev/null
wait_for_status "$EMP" "$id" PendingManager
call "$MGR" POST "/api/claims/$id/manager-decision" '{"approved":true}' >/dev/null
wait_for_status "$EMP" "$id" PendingFinance
call "$FIN" POST "/api/claims/$id/finance-decision" '{"approved":true}' >/dev/null
wait_for_status "$EMP" "$id" Approved

echo "3) Cancellation interrupts the running process"
id=$(new_claim 2000 conference)
call "$EMP" POST "/api/claims/$id/submit" >/dev/null
wait_for_status "$EMP" "$id" PendingManager
call "$EMP" POST "/api/claims/$id/cancel" >/dev/null
wait_for_status "$EMP" "$id" Cancelled

echo "4) Rules are enforced"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/claims/$id/manager-decision" \
  -H "Authorization: Bearer $MGR" -H 'Content-Type: application/json' -d '{"approved":true}')
[[ "$code" == "409" ]] || { echo "FAIL: decision on a cancelled claim returned $code"; exit 1; }
code=$(curl -s -o /dev/null -w '%{http_code}' "$API/api/claims")
[[ "$code" == "401" ]] || { echo "FAIL: request without token returned $code"; exit 1; }

curl -sf "$API/metrics" | grep -E '^claims_(submitted|completed)_total' || true
echo "PASS"
