#!/usr/bin/env bash
# So sánh mọi route GET của /api giữa server NestJS cũ và server .NET trên CÙNG một CSDL.
#   scripts/so-sanh-api.sh <url-nest> <url-dotnet> <mã-phòng-khám> <tên-đăng-nhập> <mật-khẩu>
# Đăng nhập cả hai, gọi từng route GET (kể cả route có :id lấy từ danh sách), sắp khoá JSON bằng `jq -S`
# rồi diff. Thân lỗi bỏ trường `error` (NestJS thêm tên mã HTTP, .NET không; web chỉ đọc `message`).
# Đường dẫn không tồn tại không so (NestJS trả "Cannot GET", .NET trả câu tiếng Việt — đã chủ ý).
# Trả 0 khi mọi route giống nhau, 1 nếu có khác biệt (in ra diff). Cần: curl, jq.
set -uo pipefail

if [ "$#" -ne 5 ]; then
  echo "Cách dùng: $0 <url-nest> <url-dotnet> <mã-phòng-khám> <tên-đăng-nhập> <mật-khẩu>" >&2
  exit 2
fi
NEST=${1%/}; DOTNET=${2%/}; CLINIC=$3; USER_NAME=$4; PASS=$5
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT

login() {
  curl -s -X POST "$1/api/auth/login" -H 'content-type: application/json' \
    -d "$(jq -n --arg c "$CLINIC" --arg u "$USER_NAME" --arg p "$PASS" '{clinicCode:$c,username:$u,password:$p}')" | jq -r '.token // empty'
}
TOKEN_NEST=$(login "$NEST"); TOKEN_DOTNET=$(login "$DOTNET")
[ -n "$TOKEN_NEST" ] || { echo "Không đăng nhập được vào $NEST" >&2; exit 2; }
[ -n "$TOKEN_DOTNET" ] || { echo "Không đăng nhập được vào $DOTNET" >&2; exit 2; }

FAILED=0; COUNT=0
# Gọi một route ở cả hai server; so mã HTTP và JSON đã sắp khoá. In JSON (của .NET) ra stdout để lấy id.
compare() {
  local path=$1 tag
  tag=$(echo "$path" | tr -c 'A-Za-z0-9' '_')
  local code_n code_d
  code_n=$(curl -s -o "$WORK/$tag.nest" -w '%{http_code}' "$NEST$path" -H "authorization: Bearer $TOKEN_NEST")
  code_d=$(curl -s -o "$WORK/$tag.dotnet" -w '%{http_code}' "$DOTNET$path" -H "authorization: Bearer $TOKEN_DOTNET")
  COUNT=$((COUNT + 1))
  jq -S 'if type == "object" and has("statusCode") then del(.error) else . end' "$WORK/$tag.nest" > "$WORK/$tag.nest.json" 2>/dev/null || cp "$WORK/$tag.nest" "$WORK/$tag.nest.json"
  jq -S 'if type == "object" and has("statusCode") then del(.error) else . end' "$WORK/$tag.dotnet" > "$WORK/$tag.dotnet.json" 2>/dev/null || cp "$WORK/$tag.dotnet" "$WORK/$tag.dotnet.json"
  if [ "$code_n" = "$code_d" ] && diff -u --label nest --label dotnet "$WORK/$tag.nest.json" "$WORK/$tag.dotnet.json" > "$WORK/$tag.diff"; then
    echo "GIỐNG   $code_d  $path"
  else
    FAILED=$((FAILED + 1))
    echo "KHÁC    nest=$code_n dotnet=$code_d  $path"
    cat "$WORK/$tag.diff" | sed 's/^/    /'
  fi
  cat "$WORK/$tag.dotnet.json" > "$WORK/last.json"
}

ids() { jq -r "$1" "$WORK/last.json"; }

FROM=2000-01-01; TO=2999-12-31
compare /api/auth/me
compare /api/users
compare /api/catalog/meta
compare /api/catalog/services
for id in $(ids '.. | objects | select(has("id")) | .id'); do compare "/api/catalog/services/$id"; done
compare /api/agents
compare /api/instruments
for id in $(ids '.[].id'); do compare "/api/instruments/$id/channels"; compare "/api/instruments/$id/unmapped"; done
compare "/api/samples?from=$FROM&to=$TO"
SAMPLES=$(ids '.[].sampleId')
compare "/api/samples?from=$FROM&to=$TO&status=cho-duyet"
compare "/api/samples?from=$FROM&to=$TO&q=Th%E1%BB%AD"
compare "/api/samples?from=$TO&to=$FROM"
for id in $SAMPLES; do
  compare "/api/samples/$id"
  for item in $(ids '.rows[].orderItemId'); do compare "/api/results/items/$item/history"; done
done
compare /api/agent-logs
compare "/api/agent-logs?limit=1"
compare "/api/agent-logs?direction=in"

echo
echo "Đã so $COUNT route, khác biệt: $FAILED"
[ "$FAILED" -eq 0 ]
