namespace VictusFanControl.Runtime;

internal static class InitializationCleanup
{
    internal static T Run<T>(Func<T> initialize,Action cleanup)
    {
        try{return initialize();}
        catch(Exception original)
        {
            try{cleanup();}
            catch(Exception release){throw new AggregateException("Initialization failed and cleanup remains unresolved.",original,release);}
            throw;
        }
    }
}
