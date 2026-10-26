import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.regex.*;

/** Bounded Java Pattern bridge. Input is data only; no mod or game classes are loaded. */
public final class PclQuiltPatterns {
    public static void main(String[] args) throws Exception {
        List<String> lines = Files.readAllLines(Paths.get(args[0]), StandardCharsets.UTF_8);
        int patterns = Integer.parseInt(lines.get(0));
        int ids = Integer.parseInt(lines.get(1));
        if (patterns > 512 || ids > 4096 || lines.size() != patterns + ids + 2) {
            throw new IllegalArgumentException("INPUT_LIMIT");
        }
        Base64.Decoder decoder = Base64.getDecoder();
        for (int p = 0; p < patterns; p++) {
            Pattern compiled;
            try {
                compiled = Pattern.compile(new String(decoder.decode(lines.get(2 + p)), StandardCharsets.UTF_8));
            } catch (PatternSyntaxException error) {
                System.out.println("INVALID_PATTERN:" + p);
                System.exit(2);
                return;
            }
            byte[] matches = new byte[(ids + 7) / 8];
            for (int i = 0; i < ids; i++) {
                String id = new String(decoder.decode(lines.get(2 + patterns + i)), StandardCharsets.UTF_8);
                if (compiled.matcher(id).matches()) {
                    matches[i / 8] |= 1 << (i % 8);
                }
            }
            System.out.println("ROW:" + p + ":" + Base64.getEncoder().encodeToString(matches));
        }
    }
}
