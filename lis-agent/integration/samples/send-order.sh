#!/usr/bin/env bash
# Gửi thử một chỉ định tới Agent: AGENT=http://192.168.1.20:8090 KEY=<khóa API> ./send-order.sh 2611020001
AGENT=${AGENT:-http://localhost:8090}
SID=${1:-2611020001}
curl -sS -X POST "$AGENT/api/v1/orders" -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' -d @- <<JSON
{ "sid": "$SID", "hisOrderId": "CD-THU-1",
  "patient": { "code": "BN000123", "name": "NGUYEN VAN THU", "dob": "1985-04-12", "gender": "male" },
  "tests": [ { "code": "GLU", "name": "Glucose" }, { "code": "URE", "name": "Ure" } ],
  "doctor": "BS. THU", "room": "Phòng khám 01" }
JSON
echo
