namespace WeighStation.Core.Abstractions;

/// <summary>A reference-book record with the same plate or name already exists.</summary>
public sealed class DuplicateException(string message) : Exception(message);
