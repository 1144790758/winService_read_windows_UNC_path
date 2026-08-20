import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDateTime;

/**
 * 测试程序:循环读取指定路径(支持本地路径或 \\server\share UNC 路径)的文件并打印内容。
 * 用法: java Main <文件路径> [间隔秒数,默认10]
 */
public class Main {
    public static void main(String[] args) {
        String target = args.length > 0 ? args[0] : "\\\\server\\share\\sample.txt";
        int intervalSeconds = args.length > 1 ? Integer.parseInt(args[1]) : 10;

        System.out.println("[java-test] started, pid=" + ProcessHandle.current().pid()
                + ", target=" + target + ", interval=" + intervalSeconds + "s");
        System.out.println("[java-test] user=" + System.getProperty("user.name")
                + ", os=" + System.getProperty("os.name"));

        Runtime.getRuntime().addShutdownHook(new Thread(
                () -> System.out.println("[java-test] shutdown hook invoked, exiting")));

        while (true) {
            try {
                byte[] bytes = Files.readAllBytes(Path.of(target));
                String content = new String(bytes).trim();
                System.out.println("[" + LocalDateTime.now() + "] read OK, " + bytes.length
                        + " bytes, content: " + content);
            } catch (Exception e) {
                System.err.println("[" + LocalDateTime.now() + "] read FAILED: " + e);
            }
            try {
                Thread.sleep(intervalSeconds * 1000L);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
        }
    }
}
