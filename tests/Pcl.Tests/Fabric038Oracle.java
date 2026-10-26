import net.fabricmc.loader.impl.discovery.*;

import java.io.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.zip.*;
import net.fabricmc.api.EnvType;
import net.fabricmc.loader.impl.metadata.*;
import net.fabricmc.loader.impl.game.GameProvider;

/** Uses the real Fabric 0.19.5 resolver on file-backed fixture graphs, including parent edges. */
public class Fabric038Oracle {
    static final Map<String, ModCandidateImpl> nested = new LinkedHashMap<>();
    static final Set<ModCandidateImpl> active = new LinkedHashSet<>();
    static final Map<String, Set<ModCandidateImpl>> disabled = new HashMap<>();
    static Path config;
    // Reflection reaches package-private graph construction without altering the signed official JAR.
    static ModCandidateImpl factory(String name, Class<?>[] types, Object... arguments) throws Exception {
        var method = ModCandidateImpl.class.getDeclaredMethod(name, types);
        method.setAccessible(true);
        return (ModCandidateImpl) method.invoke(null, arguments);
    }


    static ModCandidateImpl read(byte[] bytes, String name, boolean root) throws Exception {
        String hash = HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
        if (!root && nested.containsKey(hash)) return nested.get(hash);
        var entries = new HashMap<String, byte[]>();
        try (var zip = new ZipInputStream(new ByteArrayInputStream(bytes))) {
            for (ZipEntry entry; (entry = zip.getNextEntry()) != null;) {
                entries.put(entry.getName(), zip.readAllBytes());
            }
        }
        if (!entries.containsKey("fabric.mod.json")) return null;
        var metadata = ModMetadataParser.parseMetadata(new ByteArrayInputStream(entries.get("fabric.mod.json")), name, List.of(), new VersionOverrides(), new DependencyOverrides(config), false);
        var children = new ArrayList<ModCandidateImpl>();
        if (metadata.loadsInEnvironment(EnvType.CLIENT)) {
            for (var entry : metadata.getJars()) {
                var data = entries.get(entry.getFile());
                if (data == null) continue;
                var child = read(data, name + "!" + entry.getFile(), false);
                if (child != null) children.add(child);
            }
        }
        var candidate = root
            ? factory("createPlain", new Class<?>[]{List.class, LoaderModMetadata.class, boolean.class, Collection.class}, List.of(Path.of(name)), metadata, false, children)
            : factory("createNested", new Class<?>[]{String.class, long.class, LoaderModMetadata.class, boolean.class, Collection.class}, name, (long) hash.hashCode(), metadata, false, children);
        if (!root) nested.put(hash, candidate);
        var addParent = ModCandidateImpl.class.getDeclaredMethod("addParent", ModCandidateImpl.class);
        addParent.setAccessible(true);
        for (var child : children) addParent.invoke(child, candidate);
        if (metadata.loadsInEnvironment(EnvType.CLIENT)) active.add(candidate);
        else disabled.computeIfAbsent(metadata.getId(), key -> new HashSet<>()).add(candidate);
        return candidate;
    }

    public static void main(String[] args) throws Exception {
        var folder = Path.of(args[0]);
        config = folder.resolve("config");
        for (var file : Files.list(folder.resolve("mods")).sorted().toList()) {
            read(Files.readAllBytes(file), file.toString(), true);
        }
        for (var pair : Map.of("minecraft", "1.21.1", "java", "21", "fabricloader", "0.19.5").entrySet()) {
            var builtin = new GameProvider.BuiltinMod(List.of(folder), new BuiltinModMetadata.Builder(pair.getKey(), pair.getValue()).build());
            active.add(factory("createBuiltin", new Class<?>[]{GameProvider.BuiltinMod.class, VersionOverrides.class, DependencyOverrides.class}, builtin, new VersionOverrides(), new DependencyOverrides(config)));
        }
        var lines = new ArrayList<String>();
        try {
            var selected = ModResolver.resolve(active, EnvType.CLIENT, disabled);
            lines.add("satisfied");
            selected.stream().map(mod -> mod.getId() + "@" + mod.getVersion().getFriendlyString()).sorted().forEach(lines::add);
        } catch (ModResolutionException error) {
            lines.add("unsatisfiable");
        }
        Files.write(folder.resolve("official.txt"), lines);
    }
}
