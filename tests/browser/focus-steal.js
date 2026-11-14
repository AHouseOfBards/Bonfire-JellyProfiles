/*
 * Not a harness. Paste this into the browser console on a real server, with the Bonfire
 * edit profile form open, then within 8 seconds click a text box and the "Maximum rating"
 * dropdown.
 *
 * tests/run.sh only globs tests/js/*.js, so it never runs this. It answered a bug that reading
 * could not: the profile form would not take focus in any text box or dropdown, and the page
 * jumped to the top. It wraps HTMLElement.prototype.focus and logs a stack for every call,
 * restoring everything afterward. What it found:
 *
 *     onFocusIn (profiles.js)        -> HTMLElement.focus
 *     HTMLDocument.r (@mui/material) -> HTMLElement.focus
 *     onFocusIn (profiles.js)        -> ...   RangeError
 *
 * Bonfire's focus trap and MUI's passed focus back and forth until the stack overflowed.
 * Buttons worked and text boxes did not, because a click fires regardless but an input needs
 * to keep focus.
 */
(() => {
  const snapshot = {};
  const vis = el => {
    if (!el) return 'absent';
    const r = el.getBoundingClientRect();
    return `present ${Math.round(r.width)}x${Math.round(r.height)}`;
  };

  // Is a MUI modal still mounted? Its focus trap pulls focus back inside whenever it escapes,
  // which looks like "clicks work but text boxes will not take focus".
  snapshot['MUI modals mounted']   = document.querySelectorAll('.MuiModal-root').length;
  snapshot['MUI backdrops']        = document.querySelectorAll('.MuiBackdrop-root').length;
  snapshot['#app-user-menu']       = vis(document.getElementById('app-user-menu'));
  snapshot['MUI popovers']         = document.querySelectorAll('.MuiPopover-root').length;
  snapshot['aria-hidden on body?'] = document.body.getAttribute('aria-hidden') || '(none)';
  snapshot['body inert?']          = document.body.hasAttribute('inert');

  // Ours.
  snapshot['gate overlay']         = vis(document.getElementById('profiles-gate-overlay'));
  snapshot['jpf-trap-surfaces']    = document.querySelectorAll('.jpf-trap-surface').length;
  const input = document.querySelector('#profiles-gate-overlay input[type="text"], #profiles-gate-overlay input:not([type="checkbox"]):not([type="file"])');
  snapshot['a text input exists']  = vis(input);
  snapshot['...is inside our overlay'] = !!(input && input.closest('#profiles-gate-overlay'));
  console.table(snapshot);

  const orig = HTMLElement.prototype.focus;
  const calls = [];
  HTMLElement.prototype.focus = function () {
    let where = '(no stack)';
    try {
      where = (new Error().stack || '').split('\n').slice(2, 7)
        .map(l => l.trim()).filter(Boolean).join('  <-  ');
    } catch (e) { /* ignore */ }
    calls.push({
      to: this.tagName + (this.id ? '#' + this.id : '') + (this.className ? '.' + String(this.className).split(' ')[0] : ''),
      from: where
    });
    return orig.apply(this, arguments);
  };

  console.log('%cWatching focus() for 8 seconds — now click a textbox, then the Maximum rating dropdown.',
              'font-weight:bold');

  setTimeout(() => {
    HTMLElement.prototype.focus = orig;
    console.log('%cCaptured ' + calls.length + ' programmatic focus() calls:', 'font-weight:bold');
    calls.forEach((c, i) => console.log('  ' + (i + 1) + '. -> ' + c.to + '\n       ' + c.from));
    if (!calls.length) console.log('  none — nothing is calling focus(), so the cause is elsewhere.');
  }, 8000);
})()
