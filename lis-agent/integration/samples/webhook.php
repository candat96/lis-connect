<?php
// Mẫu nhận kết quả LIS Agent bằng PHP. Địa chỉ khai báo ở Agent: https://<HIS>/lis/webhook.php
$secret = '<khóa ký cấu hình ở Agent>';
$body = file_get_contents('php://input'); // NGUYÊN VĂN body để kiểm chữ ký
if ($secret !== '') {
    $want = 'sha256=' . hash_hmac('sha256', $body, $secret);
    $got = $_SERVER['HTTP_X_SIGNATURE'] ?? '';
    if (!hash_equals($want, $got)) { http_response_code(401); exit('sai chữ ký'); }
}
$p = json_decode($body, true);
if (($p['type'] ?? '') === 'ping') { exit('ok'); }
foreach ($p['results'] as $r) {
    // TODO: chống trùng theo $p['resultId']; ghi $r['testCode'] = $r['value'] cho SID $p['sid'], trạng thái CHỜ DUYỆT
}
http_response_code(200);
echo 'ok'; // 2xx = đã nhận
