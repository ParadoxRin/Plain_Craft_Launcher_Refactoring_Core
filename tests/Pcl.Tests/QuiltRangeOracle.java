import java.io.*;
import org.quiltmc.loader.api.*;
import org.quiltmc.loader.impl.metadata.qmj.V1ModMetadataReader;
public class QuiltRangeOracle {
  public static void main(String[] args) throws Exception {
    try(var reader=new BufferedReader(new FileReader(args[0]));var writer=new PrintWriter(args[1])) {
      for(String line;(line=reader.readLine())!=null;) {
        String[] fields=line.split("\t",-1);String result;
        try {result=Boolean.toString(V1ModMetadataReader.readVersionSpecifier(fields[0]).isSatisfiedBy(Version.of(fields[1])));}catch(Exception e){result="error";}
        writer.println(line+"\t"+result);
      }
    }
  }
}
