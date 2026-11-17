#!/usr/bin/env bash
# Fires 25 booking requests at the same class at the same moment and checks that
# exactly <capacity> of them are confirmed and the rest are waitlisted.
# Without the per-class lock, several requests read "1 spot left" at once and overbook.
# Usage: ./scripts/race.sh   (or PORTS=docker ./scripts/race.sh)
set -euo pipefail

if [[ "${PORTS:-local}" == "docker" ]]; then CLASSES=http://localhost:8081; BOOKINGS=http://localhost:8082
else CLASSES=http://localhost:5101; BOOKINGS=http://localhost:5102; fi

REQUESTS=25
CAPACITY=3
STARTS=$(date -u -d '+2 days' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v+2d +%Y-%m-%dT%H:%M:%SZ)

CLASS_ID=$(curl -s -X POST "$CLASSES/classes" -H 'Content-Type: application/json' \
  -d "{\"name\":\"Race Test HIIT\",\"instructor\":\"Test\",\"startsAt\":\"$STARTS\",\"durationMinutes\":30,\"capacity\":$CAPACITY}" \
  | sed -n 's/^{"id":"\([^"]*\)".*/\1/p')
echo "Created class $CLASS_ID with capacity $CAPACITY"

OUT=$(mktemp -d)
for i in $(seq 1 $REQUESTS); do
  client=$(printf 'bbbbbbbb-0000-0000-0000-%012d' "$i")
  curl -s -X POST "$BOOKINGS/bookings" -H 'Content-Type: application/json' \
    -d "{\"classId\":\"$CLASS_ID\",\"clientId\":\"$client\"}" > "$OUT/$i.json" &
done
wait

confirmed=$(cat "$OUT"/*.json | grep -o '"status":"Confirmed"' | wc -l | tr -d ' ')
waitlisted=$(cat "$OUT"/*.json | grep -o '"status":"Waitlisted"' | wc -l | tr -d ' ')
rm -rf "$OUT"

echo "Confirmed: $confirmed  Waitlisted: $waitlisted  (of $REQUESTS concurrent requests)"
if [[ "$confirmed" == "$CAPACITY" && "$waitlisted" == "$((REQUESTS - CAPACITY))" ]]; then
  echo "PASS: no overbooking"
else
  echo "FAIL: expected $CAPACITY confirmed"; exit 1
fi
