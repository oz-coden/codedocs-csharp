using System.ComponentModel;

namespace Example;

/// <summary>A user with an immutable identity.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Age">The age in years.</param>
public record User(string Name, int Age);

/// <summary>A two-dimensional point.</summary>
public readonly record struct Point(double X, double Y)
{
    /// <summary>Adds two points.</summary>
    public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y);
    public static explicit operator double(Point point) => point.X;
}

public interface IEntity { }
public interface IProducer<out T> { T Create(); }
public interface IRunner { void Run(); }

/// <summary>Provides reusable service behavior.</summary>
public class BaseService
{
    /// <summary>Starts this service.</summary>
    public virtual void Start() { }
    public void Stop() { }
    protected void Reset() { }
}

/// <summary>Stores values of <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The entity type.</typeparam>
/// <remarks>See <see cref="User"/> for an immutable data type.</remarks>
[EditorBrowsable(EditorBrowsableState.Always)]
public partial class Repository<T>(string name) : BaseService, IRunner where T : class, IEntity, new()
{
    public string RepositoryName { get; } = name;
    public required string Name { get; init; }
    public int Count { get; private set; }
    public T? this[int index] => default;
    public event EventHandler? Changed;
    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    public override void Start() { }

    /// <summary>Finds a user by ID.</summary>
    /// <param name="id">The user ID.</param>
    /// <returns>The user and whether it was found.</returns>
    public async Task<(User? user, bool found)> FindAsync(int id)
    {
        await Task.Yield();
        return (null, false);
    }

    public bool TryGet(string key, out User? user) { user = null; return false; }
    public void Log(params string[] messages) { }
    public void Connect(string host, int port = 8080) { }
    public dynamic Echo(dynamic value) => value;
    public unsafe void Copy(byte* source, byte* destination, int length) { }
    void IRunner.Run() { }
    private void Hidden() { }
    public class Nested { }
}

public partial class Repository<T>
{
    public void Save(T value) { }
}

public static class Extensions
{
    public static bool IsHello(this string value) => value == "hello";
}

public enum Status : byte { Unknown, Ready = 2 }
public delegate User? UserFactory(string name);
internal class InternalContainer { public class PublicNested { } }
