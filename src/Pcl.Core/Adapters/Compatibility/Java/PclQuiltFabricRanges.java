import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import net.fabricmc.loader.api.metadata.ModDependency;
import org.quiltmc.loader.impl.fabric.metadata.ModDependencyImpl;

/** Uses the selected Quilt Loader's own Fabric predicate parser, without loading any mod classes. */
public final class PclQuiltFabricRanges {
    private static String encode(String value) {
        return value == null ? "-" : Base64.getEncoder().encodeToString(value.getBytes(StandardCharsets.UTF_8));
    }
    public static void main(String[] args) throws Exception {
        // Library logging must never share the machine-readable result stream.
        java.io.PrintStream protocol = System.out;
        System.setOut(System.err);
        java.lang.reflect.Method convert = org.quiltmc.loader.impl.metadata.qmj.FabricModMetadataWrapper.class.getDeclaredMethod("genDepends", Collection.class);
        convert.setAccessible(true);
        List<String> lines = Files.readAllLines(Paths.get(args[0]), StandardCharsets.UTF_8);
        if (lines.size() > 4096) {
            throw new IllegalArgumentException("INPUT_LIMIT");
        }
        for (int row = 0; row < lines.size(); row++) {
            List<String> predicates = new ArrayList<>();
            if (!lines.get(row).equals("-")) {
                for (String field : lines.get(row).split(",", -1)) {
                    predicates.add(new String(Base64.getDecoder().decode(field), StandardCharsets.UTF_8));
                }
            }
            ModDependency dependency = new ModDependencyImpl(ModDependency.Kind.DEPENDS, "pcl_fixture", predicates);
            StringBuilder result = new StringBuilder("ROW:" + row + ":");
            boolean first = true;
            @SuppressWarnings("unchecked")
            Collection<org.quiltmc.loader.api.ModDependency> effective = (Collection<org.quiltmc.loader.api.ModDependency>) convert.invoke(null, Collections.singletonList(dependency));
            Collection<org.quiltmc.loader.api.VersionInterval> intervals = effective.isEmpty()
                ? Collections.emptyList()
                : ((org.quiltmc.loader.api.ModDependency.Only) effective.iterator().next()).versionRange();
            for (org.quiltmc.loader.api.VersionInterval interval : intervals) {
                if (!first) {
                    result.append(';');
                }
                first = false;
                result.append(encode(interval.getMin() == null ? null : interval.getMin().raw()));
                result.append(',').append(interval.isMinInclusive() ? '1' : '0').append(',');
                result.append(encode(interval.getMax() == null ? null : interval.getMax().raw()));
                result.append(',').append(interval.isMaxInclusive() ? '1' : '0');
            }
            protocol.println(result);
        }
    }
}
