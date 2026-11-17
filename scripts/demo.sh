#!/usr/bin/env bash
# Walks through the main booking scenarios against running services.
# Usage: ./scripts/demo.sh            (ports from a local "dotnet run")
#        PORTS=docker ./scripts/demo.sh  (ports published by docker compose)
set -euo pipefail

if [[ "${PORTS:-local}" == "docker" ]]; then
  CLASSES=http://localhost:8081; BOOKINGS=http://localhost:8082; NOTIFY=http://localhost:8083
else
  CLASSES=http://localhost:5101; BOOKINGS=http://localhost:5102; NOTIFY=http://localhost:5103
fi

PILATES=22222222-2222-2222-2222-222222222222   # capacity 2
SPIN=33333333-3333-3333-3333-333333333333      # starts in ~6 hours

ANA=aaaaaaaa-0000-0000-0000-000000000001
BEN=aaaaaaaa-0000-0000-0000-000000000002
CARA=aaaaaaaa-0000-0000-0000-000000000003
DEV=aaaaaaaa-0000-0000-0000-000000000004
ELI=aaaaaaaa-0000-0000-0000-000000000005

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

# Prints "HTTP <code>" then the body, so status codes are visible in the walkthrough.
call() {
  local method=$1 url=$2 body=${3:-}
  local args=(-s -w '\nHTTP %{http_code}\n' -X "$method" "$url" -H 'X-Correlation-Id: demo-run')
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}"
}

book() { call POST "$BOOKINGS/bookings" "{\"classId\":\"$1\",\"clientId\":\"$2\"}"; }
id_of() { sed -n 's/^{"id":"\([^"]*\)".*/\1/p' | head -1; }

step "Upcoming classes"
call GET "$CLASSES/classes"

step "Power Pilates has 2 spots: Ana and Ben get in, Cara and Dev are waitlisted"
ANA_BOOKING=$(book $PILATES $ANA | tee /dev/stderr | id_of)
book $PILATES $BEN
book $PILATES $CARA
book $PILATES $DEV

step "Ana tries to book the same class again (expect 409)"
book $PILATES $ANA

step "Roster before the cancellation"
call GET "$BOOKINGS/classes/$PILATES/roster"

step "Ana cancels, so Cara (first in line) is promoted"
call DELETE "$BOOKINGS/bookings/$ANA_BOOKING"

step "Roster after: Cara confirmed, Dev now #1 on the waitlist"
call GET "$BOOKINGS/classes/$PILATES/roster"

step "Spin 45 starts in about 6 hours: Eli books and cancels, which is flagged as a late cancel"
ELI_BOOKING=$(book $SPIN $ELI | tee /dev/stderr | id_of)
call DELETE "$BOOKINGS/bookings/$ELI_BOOKING"

step "Unknown class (expect 404)"
book 99999999-9999-9999-9999-999999999999 $ANA

step "Messages the Notifications service sent (newest first)"
sleep 1
call GET "$NOTIFY/notifications"
