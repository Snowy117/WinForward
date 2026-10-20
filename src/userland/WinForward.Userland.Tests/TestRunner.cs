namespace WinForward.Userland.Tests;

internal sealed class TestFailure : Exception
{
    public TestFailure(string message)
        : base(message)
    {
    }
}

internal static class TestRunner
{
    private static readonly List<(string Name, Action Body)> Tests = new();

    internal static void Add(string name, Action body) => Tests.Add((name, body));

    internal static int RunAll()
    {
        int failed = 0;
        foreach ((string name, Action body) in Tests)
        {
            try
            {
                body();
                Console.WriteLine($"[pass] {name}");
            }
            catch (Exception exception)
            {
                failed++;
                string detail = exception is TestFailure ? exception.Message : exception.ToString();
                Console.WriteLine($"[fail] {name}");
                Console.WriteLine($"       {detail.Replace("\n", "\n       ", StringComparison.Ordinal)}");
            }
        }

        Console.WriteLine($"[tests] total={Tests.Count} passed={Tests.Count - failed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Check
{
    internal static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailure($"{what}：期望 {Format(expected)}，实际 {Format(actual)}");
        }
    }

    internal static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new TestFailure(what);
        }
    }

    internal static void BytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new TestFailure($"{what}：期望 {Convert.ToHexString(expected)}，实际 {Convert.ToHexString(actual)}");
        }
    }

    internal static void Throws<TException>(Action action, string what)
        where TException : Exception
    {
        Exception? thrown = null;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        if (thrown is null)
        {
            throw new TestFailure($"{what}：期望抛出 {typeof(TException).Name}，实际没有抛");
        }

        if (thrown is not TException)
        {
            throw new TestFailure($"{what}：期望 {typeof(TException).Name}，实际 {thrown.GetType().Name}：{thrown.Message}");
        }
    }

    internal static void ThrowsContaining<TException>(Action action, string expectedFragment, string what)
        where TException : Exception
    {
        Exception? thrown = null;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        if (thrown is null)
        {
            throw new TestFailure($"{what}：期望抛出 {typeof(TException).Name}，实际没有抛");
        }

        if (thrown is not TException)
        {
            throw new TestFailure($"{what}：期望 {typeof(TException).Name}，实际 {thrown.GetType().Name}：{thrown.Message}");
        }

        if (!thrown.Message.Contains(expectedFragment, StringComparison.Ordinal))
        {
            throw new TestFailure($"{what}：异常消息里没有「{expectedFragment}」，实际：{thrown.Message}");
        }
    }

    private static string Format<T>(T value) => value switch
    {
        null => "null",
        byte b => $"0x{b:X2}",
        uint u => u.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };
}
