// Mẫu nhận kết quả LIS Agent — Spring Boot (Java). Địa chỉ khai báo ở Agent: https://<HIS>/lis/results
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.HexFormat;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

@RestController
class LisWebhookController {

    private static final String SECRET = "<khóa ký cấu hình ở Agent>";
    private final ObjectMapper json = new ObjectMapper();

    @PostMapping("/lis/results")
    ResponseEntity<String> receive(@RequestBody byte[] body, // NGUYÊN VĂN body để kiểm chữ ký
                                   @RequestHeader(value = "X-Signature", required = false) String signature) throws Exception {
        if (!SECRET.isEmpty()) {
            var mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(SECRET.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
            var want = "sha256=" + HexFormat.of().formatHex(mac.doFinal(body));
            if (signature == null || !MessageDigest.isEqual(want.getBytes(), signature.getBytes())) {
                return ResponseEntity.status(401).body("sai chữ ký");
            }
        }
        JsonNode p = json.readTree(body);
        if ("ping".equals(p.path("type").asText())) return ResponseEntity.ok("ok");
        String sid = p.path("sid").asText();
        for (JsonNode r : p.path("results")) {
            String code = r.path("testCode").asText();
            String value = r.path("value").asText();
            // TODO: chống trùng theo resultId; ghi kết quả theo sid + code, trạng thái CHỜ DUYỆT
        }
        return ResponseEntity.ok("ok"); // 2xx = đã nhận
    }
}
