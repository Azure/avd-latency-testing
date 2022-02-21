namespace run_test;

internal record RegionName
{
    private readonly string value;

    public RegionName(string value) => this.value = string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("Region name cannot be empty.") : value;

    public override string ToString() => value;

    public static RegionName? TryFrom(string value) => string.IsNullOrWhiteSpace(value) ? null : new(value);

    public static implicit operator string(RegionName regionName) => regionName.ToString();
}

internal record Latency
{
    private readonly uint value;

    public Latency(int value) => this.value = value < 0 ? throw new InvalidOperationException("Latency cannot be less than zero.") : (uint)value;

    public int ToInt() => (int)value;

    public override string ToString() => value.ToString();

    public static Latency? TryFrom(int value) => value < 0 ? null : new(value);

    public static Latency? TryFrom(string? value) =>
        string.IsNullOrWhiteSpace(value)
        ? null
        : int.TryParse(value, out var parsedInt)
            ? TryFrom(parsedInt)
            : null;

    public static implicit operator uint(Latency latency) => latency.value;
}