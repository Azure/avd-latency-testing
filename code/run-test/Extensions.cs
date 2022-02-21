namespace run_test;

internal static class EnumerableExtensions
{
    /// <summary>
    /// Applies <paramref name="chooser"/> to the enumerable and filters out null results.
    /// </summary>
    public static IEnumerable<U> Choose<T, U>(this IEnumerable<T> enumerable, Func<T, U?> chooser) =>
        enumerable.Select(chooser).Where(x => x is not null).Select(x => x!);

    /// <summary>
    /// Applies <paramref name="chooser"/> to the enumerable and filters out null results.
    /// </summary>
    public static IEnumerable<U> Choose<T, U>(this IEnumerable<T> enumerable, Func<T, U?> chooser) where U : struct =>
        enumerable.Select(chooser).Where(x => x.HasValue).Select(x => x!.Value);
}