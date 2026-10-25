import java.io.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.zip.*;
import net.neoforged.jarjar.selection.JarSelector;
public class JarJarOracle {
  record Node(String name,byte[] bytes) {}
  static Optional<InputStream> entry(Node node,Path path) {
    try(var zip=new ZipInputStream(new ByteArrayInputStream(node.bytes()))) {
      for(ZipEntry e;(e=zip.getNextEntry())!=null;)if(e.getName().equals(path.toString()))return Optional.of(new ByteArrayInputStream(zip.readAllBytes()));
      return Optional.empty();
    }catch(IOException e){throw new UncheckedIOException(e);}
  }
  public static void main(String[] args) throws Exception {
    var roots=new ArrayList<Node>();for(String path:Files.readAllLines(Path.of(args[0])))roots.add(new Node(path,Files.readAllBytes(Path.of(path))));
    try(var writer=new PrintWriter(args[1])) {
      try {
        var selected=JarSelector.detectAndSelect(roots,JarJarOracle::entry,(node,path)->entry(node,path).map(s->{try{return new Node(node.name()+"!/"+path,s.readAllBytes());}catch(IOException e){throw new UncheckedIOException(e);}}),Node::name,failures->new IllegalStateException("unsatisfied"));
        for(var n:selected)writer.println(HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(n.bytes()))+"\t"+n.name());
      }catch(IllegalStateException e){writer.println("unsatisfied");}
    }
  }
}
