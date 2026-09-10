/*
 * TakeTopDSH Team — shared attachment preview.
 * MIT License. Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
 *
 * Opens the bundled viewers (docx-preview / exceljs / @aiden0z/pptx-renderer)
 * for Office files, shows images, and renders text; anything else falls back to
 * a download prompt. Used by both the launcher page and the Task Assignment page
 * so clicking any attachment file name previews it consistently.
 *
 * Requires the vendor scripts (jszip, docx-preview, exceljs, pptx-loader.js).
 * Usage: TTPreview.open({ inst, path, name, token })
 */
(function () {
  'use strict';

  var IMG = ['png', 'jpg', 'jpeg', 'gif', 'bmp', 'webp', 'svg', 'ico', 'avif'];
  var TXT = ['txt', 'log', 'md', 'markdown', 'csv', 'ini', 'json', 'yml', 'yaml', 'xml',
    'html', 'htm', 'css', 'js', 'mjs', 'cjs', 'ts', 'tsx', 'jsx', 'py', 'java', 'c', 'h',
    'cpp', 'hpp', 'cs', 'go', 'rs', 'rb', 'php', 'sh', 'bash', 'sql', 'bat', 'ps1',
    'conf', 'toml', 'env', 'properties', 'gitignore'];

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }
  function extOf(name) { return (String(name).split('.').pop() || '').toLowerCase(); }
  function officeKind(e) {
    if (e === 'docx') return 'word';
    if (e === 'xlsx' || e === 'xlsm') return 'excel';
    if (e === 'pptx') return 'ppt';
    return '';
  }
  function buildUrl(base, pairs) {
    var parts = [];
    for (var i = 0; i < pairs.length; i++) {
      var k = pairs[i][0], v = pairs[i][1];
      if (v === null || v === undefined || v === '') continue;
      parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
    }
    return base + (parts.length ? '?' + parts.join('&') : '');
  }
  function waitGlobal(get, ms) {
    return new Promise(function (res) {
      var t0 = Date.now();
      (function loop() {
        if (get()) return res(true);
        if (Date.now() - t0 > ms) return res(false);
        setTimeout(loop, 100);
      })();
    });
  }

  function ensureModal() {
    var m = document.getElementById('ttPreviewModal');
    if (m) return m;
    m = document.createElement('div');
    m.id = 'ttPreviewModal';
    m.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.5);z-index:10030;display:none;align-items:center;justify-content:center;';
    m.innerHTML =
      '<div style="background:#fff;border-radius:10px;width:min(94vw,1000px);height:min(90vh,820px);display:flex;flex-direction:column;box-shadow:0 10px 40px rgba(0,0,0,.3);">'
      + '<div style="display:flex;align-items:center;gap:8px;padding:10px 14px;border-bottom:1px solid #e5e7eb;">'
      + '<strong id="ttPreviewTitle" style="flex:1;word-break:break-all;font-size:15px;"></strong>'
      + '<button id="ttPreviewClose" style="padding:5px 12px;background:#64748b;color:#fff;border:0;border-radius:6px;cursor:pointer;font-size:14px;">×</button>'
      + '</div>'
      + '<div id="ttPreviewBody" style="flex:1;overflow:auto;padding:12px 16px;background:#fbfbfc;"></div>'
      + '</div>';
    document.body.appendChild(m);
    var close = function () { m.style.display = 'none'; m.querySelector('#ttPreviewBody').innerHTML = ''; };
    m.querySelector('#ttPreviewClose').onclick = close;
    m.addEventListener('click', function (ev) { if (ev.target === m) close(); });
    return m;
  }

  function xlsxTable(wb) {
    var html = '';
    try {
      wb.eachSheet(function (ws) {
        html += '<div style="margin:10px 0 4px;font-weight:600;font-size:13px;">' + esc(ws.name) + '</div>';
        html += '<div style="overflow:auto;margin:0 0 14px;"><table style="border-collapse:collapse;font-size:12px;">';
        ws.eachRow({ includeEmpty: false }, function (row) {
          html += '<tr>';
          row.eachCell({ includeEmpty: true }, function (cell) {
            var v = '';
            try { v = cell.text != null ? String(cell.text) : ''; } catch (e) { v = ''; }
            html += '<td style="border:1px solid #e5e7eb;padding:3px 7px;white-space:nowrap;vertical-align:top;">' + esc(v) + '</td>';
          });
          html += '</tr>';
        });
        html += '</table></div>';
      });
    } catch (e) { }
    return html || '<div style="padding:20px;color:#6b7280;">(empty)</div>';
  }

  var LOADING = '<div style="padding:20px;color:#6b7280;">Loading…</div>';

  function open(opts) {
    opts = opts || {};
    var name = opts.name || String(opts.path || '').split('/').pop();
    var ext = extOf(name);
    var m = ensureModal();
    var title = m.querySelector('#ttPreviewTitle');
    var body = m.querySelector('#ttPreviewBody');
    title.textContent = name;
    body.style.background = '#fff';
    body.innerHTML = LOADING;
    m.style.display = 'flex';

    var base = [['inst', opts.inst], ['launcher_token', opts.token]];
    var readUrl = buildUrl('/api/files/read', base);
    var dlUrl = buildUrl('/api/files/download', base.concat([['path', opts.path]]));
    var officeUrl = buildUrl('/api/files/download', base.concat([['path', opts.path], ['inline', 'true']]));

    function postRead() {
      return fetch(readUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'same-origin',
        body: JSON.stringify({ path: opts.path })
      }).then(function (r) { return r.json().catch(function () { return {}; }); });
    }
    function promptDownload() {
      var ask = opts.askText || "This file type can't be previewed. Download it?";
      if (window.confirm(ask + ' ' + name + '?')) window.open(dlUrl, '_blank');
      m.style.display = 'none';
    }
    function fail(e) {
      body.innerHTML = '<div style="padding:20px;color:#b91c1c;">' + esc((opts.errorText || 'Preview failed') + ': ' + ((e && e.message) || e)) + '</div>';
    }

    var kind = officeKind(ext);
    if (kind) {
      fetch(officeUrl, { credentials: 'same-origin' })
        .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.arrayBuffer(); })
        .then(function (buf) {
          if (kind === 'word') {
            if (!window.docx) throw new Error('docx-preview not loaded');
            body.innerHTML = '';
            return window.docx.renderAsync(buf, body, null, { inWrapper: true, className: 'docx-preview', breakPages: true });
          }
          if (kind === 'excel') {
            if (!window.ExcelJS) throw new Error('exceljs not loaded');
            var wb = new window.ExcelJS.Workbook();
            return wb.xlsx.load(buf).then(function () { body.innerHTML = xlsxTable(wb); });
          }
          if (kind === 'ppt') {
            return waitGlobal(function () { return !!window.TTPptx; }, 10000).then(function (ok) {
              if (!ok) throw new Error('pptx renderer not loaded');
              body.innerHTML = '';
              return window.TTPptx.open(buf, body);
            });
          }
        })
        .catch(fail);
      return;
    }
    if (IMG.indexOf(ext) >= 0) {
      postRead().then(function (d) {
        if (d && d.ok && d.type === 'image' && d.dataUri) {
          body.style.background = '#fbfbfc';
          body.innerHTML = '<div style="display:flex;justify-content:center;padding:6px;"><img style="max-width:100%;max-height:74vh;" src="' + d.dataUri + '" /></div>';
        } else { promptDownload(); }
      }).catch(promptDownload);
      return;
    }
    if (TXT.indexOf(ext) >= 0) {
      postRead().then(function (d) {
        if (!(d && d.ok)) { promptDownload(); return; }
        if (ext === 'md' || ext === 'markdown') {
          body.innerHTML = '<div id="ttPreviewMd" style="line-height:1.6;font-size:14px;"></div>';
          var el = body.querySelector('#ttPreviewMd');
          try { el.innerHTML = window.marked ? window.marked.parse(d.content || '') : esc(d.content || ''); }
          catch (e) { el.textContent = d.content || ''; }
        } else {
          body.innerHTML = '<pre style="white-space:pre-wrap;word-break:break-word;font-size:13px;margin:0;"></pre>';
          body.querySelector('pre').textContent = d.content || '';
        }
      }).catch(promptDownload);
      return;
    }
    promptDownload();
  }

  window.TTPreview = { open: open, officeKind: officeKind };
})();
