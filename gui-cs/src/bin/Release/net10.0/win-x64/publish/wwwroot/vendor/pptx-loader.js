// Browser loader for .pptx preview.
// Imports the Apache-2.0 @aiden0z/pptx-renderer standalone browser bundle
// (which bundles JSZip + ECharts) and exposes a minimal global so the
// classic script in index.html can render PowerPoint files.
import { PptxViewer, RECOMMENDED_ZIP_LIMITS } from './pptx-renderer/aiden0z-pptx-renderer.browser.es.js';

window.TTPptx = {
  open: function (arrayBuffer, container) {
    return PptxViewer.open(arrayBuffer, container, {
      zipLimits: RECOMMENDED_ZIP_LIMITS,
      listOptions: { windowed: true },
    });
  },
};
