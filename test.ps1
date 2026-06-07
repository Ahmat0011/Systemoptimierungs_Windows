$code = @"
public class Test {
    public static void Run() {
        System.Console.WriteLine("Hello from Add-Type!");
    }
}
"@
Add-Type -TypeDefinition $code
[Test]::Run()
