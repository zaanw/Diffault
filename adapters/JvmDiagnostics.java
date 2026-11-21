import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Pattern;

class JvmDiagnostics {
    private static final Pattern MAVEN = Pattern.compile(
        "^\\[(ERROR|WARNING)]\\s+(.+?\\.java):\\[(\\d+),(\\d+)]\\s*(.+)$");
    private static final Pattern JAVAC = Pattern.compile(
        "^(.+?\\.java):(\\d+):\\s*(error|warning):\\s*(.+)$");
    private static final Pattern EXCEPTION = Pattern.compile(
        "^(?:Exception in thread \"[^\"]+\"\\s+|Caused by:\\s+)?([\\w.$]+(?:Exception|Error|Throwable))(?::\\s*(.*))?$");
    private static final Pattern FRAME = Pattern.compile(
        "^\\s+at\\s+[^\\s(]+\\(([^():]+\\.java):(\\d+)\\)$");

    record Diagnostic(String severity, String file, Integer line, Integer column, String code,
                      String message, String original) {
        String json() {
            return "{\"language\":\"java\",\"severity\":" + quote(severity)
                + ",\"file\":" + quote(file) + ",\"line\":" + line + ",\"column\":" + column
                + ",\"code\":" + quote(code) + ",\"message\":" + quote(message)
                + ",\"original\":" + quote(original) + ",\"count\":1}";
        }
    }

    static Integer position(String raw) {
        try {
            int value = Integer.parseInt(raw);
            return value > 0 ? value : null;
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    static List<Diagnostic> scan(BufferedReader reader) throws Exception {
        var results = new ArrayList<Diagnostic>();
        Diagnostic pending = null;
        for (String raw; (raw = reader.readLine()) != null;) {
            if (pending != null) {
                var frame = FRAME.matcher(raw);
                if (frame.matches()) {
                    results.add(new Diagnostic(pending.severity, frame.group(1), position(frame.group(2)), null,
                        pending.code, pending.message, pending.original + "\n" + raw));
                    pending = null;
                    continue;
                }
                results.add(pending);
                pending = null;
            }

            var maven = MAVEN.matcher(raw);
            var javac = JAVAC.matcher(raw);
            var exception = EXCEPTION.matcher(raw);
            if (maven.matches()) {
                results.add(new Diagnostic(maven.group(1).equals("ERROR") ? "error" : "warning",
                    maven.group(2), position(maven.group(3)), position(maven.group(4)), null,
                    maven.group(5), raw));
            } else if (javac.matches()) {
                results.add(new Diagnostic(javac.group(3), javac.group(1), position(javac.group(2)), null,
                    null, javac.group(4), raw));
            } else if (exception.matches()) {
                pending = new Diagnostic("error", null, null, null, exception.group(1),
                    exception.group(2) == null ? exception.group(1) : exception.group(2), raw);
            }
        }
        if (pending != null) results.add(pending);
        return results;
    }

    private static String quote(String value) {
        if (value == null) return "null";
        var out = new StringBuilder("\"");
        for (char c : value.toCharArray()) {
            if (c == '"' || c == '\\') out.append('\\').append(c);
            else if (c < 32) out.append(String.format("\\u%04x", (int) c));
            else out.append(c);
        }
        return out.append('"').toString();
    }

    public static void main(String[] args) throws Exception {
        try (var input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            var json = "[" + String.join(",", scan(input).stream().map(Diagnostic::json).toList()) + "]\n";
            System.out.write(json.getBytes(StandardCharsets.UTF_8));
        }
    }
}
