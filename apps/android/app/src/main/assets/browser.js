(() => {
  if (window.minirp) return 'ready';
  const elements = new Map();
  let counter = 0;
  function snapshot() {
    const lines = [];
    for (const el of document.querySelectorAll('h1,h2,h3,p,a,button,input,textarea,select,[role="button"],[role="link"]')) {
      if (!el.getClientRects().length) continue;
      let ref = el.dataset.minirpRef;
      if (!ref) { ref = 'e' + ++counter; el.dataset.minirpRef = ref; elements.set(ref, el); }
      const label = (el.innerText || el.getAttribute('aria-label') || el.placeholder || '').trim();
      if (label) lines.push(`[${ref}] ${el.tagName.toLowerCase()} ${label}`);
    }
    return {url:location.href,title:document.title,snapshot:lines.join('\n')};
  }
  window.minirp = {
    content: '',
    capture() {
      const clone = document.body.cloneNode(true);
      clone.querySelectorAll('script,style,noscript,template,svg').forEach(el => el.remove());
      clone.querySelectorAll('a[href],img[src]').forEach(el => {
        for (const attr of ['href','src']) if(el.hasAttribute(attr)) {
          try { el.setAttribute(attr,new URL(el.getAttribute(attr),location.href).href); } catch {}
        }
      });
      this.content = new TurndownService({headingStyle:'atx',codeBlockStyle:'fenced'}).turndown(clone);
      const frameErrors = [];
      for (const frame of document.querySelectorAll('iframe')) {
        try {
          if (!frame.contentDocument?.body) throw Error('Frame body is unavailable');
          this.content += '\n\n' + new TurndownService().turndown(frame.contentDocument.body);
        } catch { frameErrors.push({url:frame.src, error:'Embedded frame could not be captured; open it separately if relevant'}); }
      }
      return {url:location.href,title:document.title,length:this.content.length,frame_errors:frameErrors};
    },
    perform(tool,args) {
      const el = elements.get(args.ref);
      if (tool === 'browser_click') { if(!el) throw Error('Unknown ref; take a snapshot'); el.click(); }
      if (tool === 'browser_type') { if(!el) throw Error('Unknown ref'); el.focus(); const setter=Object.getOwnPropertyDescriptor(el.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype,'value').set; setter.call(el,args.text); el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); if(args.submit) el.form?.requestSubmit(); }
      if (tool === 'browser_scroll') window.scrollBy(0,args.direction === 'up' ? -750 : 750);
      if (tool === 'browser_press_key') { document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:args.key,bubbles:true})); if(args.key === 'Enter') document.activeElement.form?.requestSubmit(); }
      return snapshot();
    }
  };
  return 'ready';
})();
