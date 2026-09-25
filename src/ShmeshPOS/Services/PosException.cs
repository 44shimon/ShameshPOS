namespace ShmeshPOS.Services;

/// <summary>
/// A failure whose <see cref="Exception.Message"/> is safe to show to the cashier.
/// Anything else is treated as unexpected: logged in full and shown as a generic message.
/// </summary>
public class PosException(string userMessage, Exception? innerException = null) : Exception(userMessage, innerException);
