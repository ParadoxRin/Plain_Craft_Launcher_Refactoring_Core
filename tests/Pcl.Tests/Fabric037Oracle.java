import java.nio.file.*;
import java.util.*;
import net.fabricmc.loader.impl.metadata.*;
import net.fabricmc.loader.api.Version;
import net.fabricmc.api.EnvType;

/** Calls the actual fixed Fabric Loader parser; no game code, account or network is loaded. */
public class Fabric037Oracle {
    public static void main(String[] args) throws Exception {
        var folder = Path.of(args[0]);
        try (var stream = Files.newInputStream(folder.resolve("metadata.json"))) {
            var mod = ModMetadataParser.parseMetadata(stream, "fixture", List.of(), new VersionOverrides(), new DependencyOverrides(folder), false);
            var lines = new ArrayList<String>();
            lines.add("schema=" + mod.getSchemaVersion());
            lines.add("client=" + mod.loadsInEnvironment(EnvType.CLIENT));
            for (var dep : mod.getDependencies()) {
                var bits = new StringBuilder();
                for (var version : Files.readAllLines(folder.resolve("versions.txt"))) bits.append(dep.matches(Version.parse(version)) ? '1' : '0');
                lines.add(dep.getKind().getKey() + ":" + dep.getModId() + ":" + bits);
            }
            Collections.sort(lines);
            Files.write(folder.resolve("official.txt"), lines);
        }
    }
}
