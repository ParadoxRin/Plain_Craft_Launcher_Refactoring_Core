import java.nio.file.*;
import java.util.*;
import org.quiltmc.loader.api.ModDependency;
import org.quiltmc.loader.impl.metadata.qmj.QuiltOverrides;
import org.quiltmc.loader.impl.metadata.qmj.FabricModMetadataWrapper;
import org.quiltmc.loader.impl.fabric.metadata.FabricModMetadataReader;
import org.quiltmc.loader.impl.plugin.quilt.StandardQuiltPlugin;

/** Execute official selector matching and dependency transformations; compare semantic sets, not textual JSON. */
public class Quilt039Oracle {
    private static QuiltOverrides.ModOverrides only(QuiltOverrides value) {
        return value.patternOverrides.values().iterator().next();
    }
    public static void main(String[] args) throws Exception {
        Path folder = Path.of(args[0]);
        QuiltOverrides rules = new QuiltOverrides(folder.resolve("config/quilt-loader-overrides.json"));
        Set<ModDependency> depends;
        Set<ModDependency> breaks;
        if (Boolean.parseBoolean(args[3])) {
            var metadata = new FabricModMetadataWrapper(FabricModMetadataReader.parseMetadata(folder.resolve("fabric.mod.json")));
            depends = new HashSet<>(metadata.depends());
            breaks = new HashSet<>(metadata.breaks());
        } else {
            var input = only(new QuiltOverrides(folder.resolve("input.json")));
            depends = new HashSet<>(input.dependsOverrides.additions);
            breaks = new HashSet<>(input.breakOverrides.additions);
        }
        var method = StandardQuiltPlugin.class.getDeclaredMethod("replace", boolean.class, QuiltOverrides.SpecificOverrides.class, Collection.class);
        method.setAccessible(true);
        var path = rules.pathOverrides.get(args[2]);
        if (path != null) {
            method.invoke(null, true, path.dependsOverrides, depends);
            method.invoke(null, true, path.breakOverrides, breaks);
        }
        for (var entry : rules.patternOverrides.entrySet()) {
            if (entry.getKey().matcher(args[1]).matches()) {
                method.invoke(null, false, entry.getValue().dependsOverrides, depends);
                method.invoke(null, false, entry.getValue().breakOverrides, breaks);
            }
        }
        var expected = only(new QuiltOverrides(folder.resolve("expected.json")));
        if (!depends.equals(new HashSet<>(expected.dependsOverrides.additions)) || !breaks.equals(new HashSet<>(expected.breakOverrides.additions))) {
            throw new AssertionError("Official result differs: " + depends + " / expected " + expected.dependsOverrides.additions);
        }
        System.out.println("PASS official selectors and effective constraints");
    }
}
