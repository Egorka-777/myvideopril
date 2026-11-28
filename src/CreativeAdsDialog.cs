using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    public static class CreativeAdsDialog {
        public static List<AssemblyInsertion> Edit(IWin32Window owner, List<AssemblyInsertion> items, int width, int height, Func<Bitmap> background) {
            var template = new AssemblyTemplate {
                Width = width,
                Height = height,
                Insertions = AssemblyInsertions.Copy(items)
            };
            return AssemblyWindow.ShowInsertions(owner, template, background, () => { }) == DialogResult.OK
                ? AssemblyInsertions.Copy(template.Insertions)
                : null;
        }
    }
}
