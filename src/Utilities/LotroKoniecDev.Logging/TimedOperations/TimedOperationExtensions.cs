using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Logging.TimedOperations;

public static class TimedOperationExtensions
{
    extension(ILogger logger)
    {
        public IDisposable BeginTimedOperation(
            long? slowThresholdMs,
            string messageTemplate)
        {
            return new TimedOperation(logger, slowThresholdMs, messageTemplate);
        }

        public IDisposable BeginTimedOperation<T0>(
            long? slowThresholdMs,
            string messageTemplate,
            T0 arg0)
        {
            return new TimedOperation<T0>(logger, slowThresholdMs, messageTemplate, arg0);
        }

        public IDisposable BeginTimedOperation<T0, T1>(
            long? slowThresholdMs,
            string messageTemplate,
            T0 arg0,
            T1 arg1)
        {
            return new TimedOperation<T0, T1>(logger, slowThresholdMs, messageTemplate, arg0, arg1);
        }
    }
}
