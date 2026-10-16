using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace BubbleClusters
{
    public class BubbleClustersInfo : GH_AssemblyInfo
    {
        public override string Name { get { return "Bubble Clusters"; } }
        public override string Description
        {
            get { return "Physically accurate soap-bubble clusters: minimum film area at fixed volumes, so Plateau's laws come out of the solve."; }
        }
        public override Guid Id { get { return new Guid("6d1f3b9e-2c4a-4f7e-9b1d-3a5c8e2f7041"); } }
        public override string AuthorName { get { return "Alex Lehrecke / Pneuhaus"; } }
        public override string AuthorContact { get { return "github.com/alehrecke/bubble-clusters"; } }
        public override string Version { get { return "0.6.0"; } }
        public override Bitmap Icon { get { return null; } }
    }
}
