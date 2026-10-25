import java.nio.file.*;
import java.util.*;
import org.quiltmc.loader.api.ModDependency;
import org.quiltmc.loader.impl.metadata.qmj.QuiltOverrides;
import org.quiltmc.loader.impl.plugin.quilt.StandardQuiltPlugin;

/** 仅运行官方的覆盖解析和集合变换方法；不创建游戏会话、不请求身份令牌。 */
public class QuiltOverrideOracle {
    public static void main(String[] args) throws Exception {
        var parsed = new QuiltOverrides(Path.of(args[0]));
        var byId = new HashMap<String, QuiltOverrides.ModOverrides>();
        for (var entry : parsed.patternOverrides.entrySet()) byId.put(entry.getKey().pattern(), entry.getValue());
        var input = new HashSet<ModDependency>(byId.get("input").dependsOverrides.additions);
        // 反射调用被测加载器本身的私有实现；不在测试中重写被测算法。
        var method = StandardQuiltPlugin.class.getDeclaredMethod("replace", boolean.class, QuiltOverrides.SpecificOverrides.class, Collection.class);
        method.setAccessible(true);
        method.invoke(null, false, byId.get("rule").dependsOverrides, input);
        var expected = new HashSet<ModDependency>(byId.get("expected").dependsOverrides.additions);
        if (!input.equals(expected)) throw new AssertionError("Official Quilt result differs: " + args[0]);
        System.out.println("PASS " + args[0]);
    }
}
