using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace DuoClick
{
    internal static class AppTrayIcon
    {
        internal const string ResourceName = "DuoClick.AppIcon.ico";

        internal static Icon Load(out bool loadedEmbeddedResource)
        {
            return Load(Assembly.GetExecutingAssembly(), out loadedEmbeddedResource);
        }

        internal static Icon Load(Assembly assembly, out bool loadedEmbeddedResource)
        {
            Icon embedded = TryLoadEmbedded(assembly);
            if (embedded != null)
            {
                loadedEmbeddedResource = true;
                return embedded;
            }

            loadedEmbeddedResource = false;
            return (Icon)SystemIcons.Application.Clone();
        }

        internal static Icon TryLoadEmbedded(Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");

            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    return null;

                using (Icon source = new Icon(stream))
                    return (Icon)source.Clone();
            }
        }
    }
}
