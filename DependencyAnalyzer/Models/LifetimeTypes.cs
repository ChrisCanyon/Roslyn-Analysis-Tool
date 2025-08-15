namespace DependencyAnalyzer.Models
{
    //there are ordered by shortest to longest lived
    //a simple > or < comparison tells you if you are longer or shorter lived
    public enum LifetimeTypes
    {
        Controller,
        Transient,
        PerWebRequest,
        Singleton,
        Unregistered
    }
}
