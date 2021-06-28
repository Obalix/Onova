using System;
using System.Reflection;

namespace Onova.Models
{
    /// <summary>
    /// Contains information about an assembly.
    /// </summary>
    public partial class AssemblyMetadata
    {
		/// <summary>
        /// Name of the Onova Directory
        /// </summary>
		public string Name { get; }

        /// <summary>
        /// Assembly name. (i.e. the executable to be started)
        /// </summary>
        public string Executable { get; }

        /// <summary>
        /// Assembly version.
        /// </summary>
        public Version Version { get; }

        /// <summary>
        /// Assembly file path.
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="AssemblyMetadata"/>.
        /// </summary>
        /// <param name="executable">The name of the executable.</param>
        /// <param name="filePath">The file path to the executable.</param>
        /// <param name="version">The assembly version.</param>
        /// <param name="name">Optional name of the Onova applciation folder. If null the executable name will be used.</param>
        public AssemblyMetadata(string executable, Version version, string filePath, string? name = null)
        {
            this.Executable = executable;
            this.Name = name ?? executable;
            this.Version = version;
            this.FilePath = filePath;
        }
    }

    public partial class AssemblyMetadata
    {
        /// <summary>
        /// Extracts assembly metadata from given assembly.
        /// The specified path is used to override the executable file path in case the assembly is not meant to run directly.
        /// </summary>
        public static AssemblyMetadata FromAssembly(Assembly assembly, string assemblyFilePath, string? name = null)
        {
            var executable = assembly.GetName().Name!;
            var version = assembly.GetName().Version!;
            var filePath = assemblyFilePath;

            return new AssemblyMetadata(executable, version, filePath, name);
        }

        /// <summary>
        /// Extracts assembly metadata from given assembly.
        /// </summary>
        public static AssemblyMetadata FromAssembly(Assembly assembly, string? name = null) => FromAssembly(assembly, assembly.Location, name);

        /// <summary>
        /// Extracts assembly metadata from entry assembly.
        /// </summary>
        public static AssemblyMetadata FromEntryAssembly(string? name = null)
        {
            var assembly = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("Can't get entry assembly.");
            return FromAssembly(assembly, name);
        }
    }
}