/*
 * TakeTopDshTeam — drag support + ⣿ grips for the Task-Assignment page (the pane's
 * "Tasks I created" tab), so it behaves exactly like the "Tasks to handle" tab:
 *
 *   task name (in the list)        -> "task name"
 *   task preview layer             -> title / content text
 *   feedback layer                 -> title / editor / every record's content
 *   attachment file names          -> "TaskData/Doc/<file>"
 *                                     "TaskData/Feedback/<seq>/<file>"
 *
 * Every payload follows the product-wide rule: the dragged value is the
 * DOUBLE-QUOTED name or workspace-relative path, and a leading ", " is added when
 * the DSH composer already holds text (so several drags read as a list).
 *
 * MIT License. Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
 */
(function () {
  'use strict';

  var NAME = 'a.task-name-link';            // task name in the list
  var FILE = 'a.task-file-link';            // data-file (own attachments)
  var FBF = 'a.task-fbfile-link';           // data-name (feedback attachments, seq on the row)
  var FB2F = 'a.fb2-file-link';             // data-f (feedback layer attachments)
  var TITLE = '#viewTitle,#fb2Title';       // both layers' titles
  var BODY = '#viewContent,#fb2Editor';     // task content / feedback editor
  var CELL = '#fb2Body tr>td:nth-child(2)'; // feedback record content
  var GRIP = '.fm-drag-handle';

  function gripSpan() {
    var s = document.createElement('span');
    s.className = 'fm-drag-handle';
    s.style.cssText = 'display:inline-block;width:9px;height:15px;vertical-align:middle;margin-right:3px;flex:0 0 auto;cursor:grab;'
      + 'background:url("data:image/svg+xml;utf8,<svg xmlns=\'http://www.w3.org/2000/svg\' viewBox=\'0 0 10 16\'>'
      + '<circle cx=\'3\' cy=\'3\' r=\'1.4\'/><circle cx=\'7\' cy=\'3\' r=\'1.4\'/>'
      + '<circle cx=\'3\' cy=\'8\' r=\'1.4\'/><circle cx=\'7\' cy=\'8\' r=\'1.4\'/>'
      + '<circle cx=\'3\' cy=\'13\' r=\'1.4\'/><circle cx=\'7\' cy=\'13\' r=\'1.4\'/></svg>") center/contain no-repeat;';
    // No chat in this edition: the handle only mentions the DSH dialog.
    s.title = /^en/i.test(lang()) ? 'Drag onto the DSH dialog' : '可拖到右侧 DSH 对话框';
    return s;
  }
  function lang() {
    try { var m = document.cookie.match(/(?:^|;\s*)tt_lang=([^;]+)/); if (m) return decodeURIComponent(m[1]); } catch (e) { }
    try { return localStorage.getItem('tt_lang') || ''; } catch (e) { }
    return '';
  }
  // Does the DSH composer (the sibling same-origin frame) already hold text?
  function composerHasText() {
    try {
      var par = window.parent;
      if (!par || par === window) return false;
      var fr = par.document.getElementById('dshPane');
      var doc = fr && (fr.contentDocument || (fr.contentWindow && fr.contentWindow.document));
      if (!doc) return false;
      var el = doc.querySelector('[contenteditable="true"]') || doc.querySelector('[contenteditable]') || doc.querySelector('textarea');
      if (!el) return false;
      var t = (typeof el.value === 'string') ? el.value : (el.textContent || '');
      return t.replace(/\u200b/g, '').trim().length > 0;
    } catch (e) { return false; }
  }
  function dragStart(ev, value, path, inst) {
    if (!value) return;
    var pre = composerHasText() ? ', ' : '';
    try { ev.dataTransfer.setData('text/plain', pre + '"' + value + '"'); } catch (e) { }
    if (path) {
      try { ev.dataTransfer.setData('application/x-tt-fm-path', path); } catch (e) { }
      try { ev.dataTransfer.setData('application/x-tt-fm-name', path.split('/').pop()); } catch (e) { }
      // The workspace that actually holds the file (the task's owner), so the chat
      // can download it even when the task belongs to another member.
      if (inst) { try { ev.dataTransfer.setData('application/x-tt-fm-inst', inst); } catch (e) { } }
    }
    try { ev.dataTransfer.effectAllowed = 'copy'; } catch (e) { }
  }
  // Workspace holding the attachment: the row carries the task's owner.
  function instOf(el) {
    if (el.matches(FB2F)) return window.fb2Inst || '';
    var row = el.closest('tr');
    return (row && row.getAttribute('data-member')) || '';
  }
  // The stored attachment references may already carry their folder ("Doc/x.png"),
  // so only the file name is kept - the folder is always added here.
  function base(v) { return String(v == null ? '' : v).split('/').pop(); }
  // The value each element carries, or '' when it is not a drag source.
  function valueOf(el) {
    if (el.matches(NAME)) return (el.textContent || '').trim();
    if (el.matches(FILE)) return base(el.getAttribute('data-file')) ? 'TaskData/Doc/' + base(el.getAttribute('data-file')) : '';
    if (el.matches(FBF)) {
      var row = el.closest('tr');
      var seq = (row && row.getAttribute('data-seq')) || '0';
      return base(el.getAttribute('data-name')) ? 'TaskData/Feedback/' + seq + '/' + base(el.getAttribute('data-name')) : '';
    }
    if (el.matches(FB2F)) return base(el.getAttribute('data-f')) ? 'TaskData/Feedback/' + (window.fb2Seq || 0) + '/' + base(el.getAttribute('data-f')) : '';
    if (el.matches(TITLE) || el.matches(BODY) || el.matches(CELL)) return (el.innerText || el.textContent || '').trim();
    return '';
  }
  var SEL = [NAME, FILE, FBF, FB2F, TITLE, BODY, CELL].join(',');
  // Elements that get the ⣿ grip (the cells/editors are already draggable blocks).
  var GRIPPED = [NAME, FILE, FBF, FB2F, TITLE, BODY].join(',');

  function ensure(el) {
    if (!el || el.nodeType !== 1) return;
    if (el.getAttribute('data-ttdrag') && el.querySelector(':scope > ' + GRIP)) return;
    el.setAttribute('data-ttdrag', '1');
    var v = valueOf(el);
    if (!v) return;
    el.setAttribute('draggable', 'true');
    if (!el._ttDragBound) {
      el._ttDragBound = true;
      el.addEventListener('dragstart', function (ev) {
        var val = valueOf(el);
        dragStart(ev, val, val.indexOf('/') >= 0 ? val : '', instOf(el));
      });
    }
    if (el.matches(GRIPPED) && !el.querySelector(':scope > ' + GRIP)) el.insertBefore(gripSpan(), el.firstChild);
  }
  function scan(root) {
    try { (root.querySelectorAll ? root.querySelectorAll(SEL) : []).forEach(ensure); } catch (e) { }
  }
  function start() {
    scan(document);
    setInterval(function () { scan(document); }, 1000);
    new MutationObserver(function () { scan(document); }).observe(document.body, { childList: true, subtree: true });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();

  window.TTTaskDrag = { scan: scan };
})();
