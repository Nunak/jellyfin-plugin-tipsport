// Exercise the actual provider scripts with a synthetic DOM; no browser, network or real credentials.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync(new URL('../src/Jellyfin.Plugin.Tipsport/ProviderLoginForm.cs', import.meta.url), 'utf8');
const script = name => {
  const match = source.match(new RegExp('internal const string ' + name + ' = """\\n([\\s\\S]*?)\\n\\s*""";'));
  assert.ok(match, name); return match[1];
};
const control = (textContent, shown = true) => ({ textContent, getClientRects: () => shown ? [{width: 10, height: 10}] : [] });
const style = e => ({visibility: e.hiddenByStyle ? 'hidden' : 'visible', display: 'block'});
const signedIn = (controls, actualOrigin = 'https://www.tipsport.cz') => {
  const window = {}; window.top = window;
  return vm.runInNewContext('(' + script('AuthenticatedScript') + ')("https://www.tipsport.cz")', {
    document: {querySelectorAll: () => controls}, window, location: {origin: actualOrigin}, getComputedStyle: style
  });
};
assert.equal(signedIn([control('Přihlásit')]), false);
assert.equal(signedIn([control('Vložit peníze'), control('Přihlásit')]), false);
assert.equal(signedIn([control('Vložit peníze', false)]), false);
assert.equal(signedIn([]), false);
assert.equal(signedIn([control('Vložit peníze'), control('Streamy')]), true);
assert.equal(signedIn([control('Odhlásiť sa')]), true);
assert.equal(signedIn([control('Vložit peníze')], 'https://www.tipsport.cz.evil.test'), false);
assert.equal(signedIn([control('Vložit peníze')], 'https://www.tipsport.sk'), false);

function formCase({origin = 'https://www.tipsport.cz', href = origin + '/', readyState = 'complete', captcha = false, providerError = false} = {}) {
  let clicks = 0;
  const button = {...control('Přihlásit se'), disabled: true, click() {clicks++;}};
  class Input {
    constructor() {this.content = '';}
    get value() {return this.content;}
    set value(v) {this.content = v;}
    getClientRects() {return [{width: 10, height: 10}];}
    dispatchEvent() {button.disabled = !(username.value && password.value);}
    closest(selector) {return selector === 'form' ? {querySelectorAll: () => [button]} : null;}
  }
  const username = new Input(), password = new Input();
  const context = {
    window: {}, location: {origin, href}, HTMLInputElement: Input, Event: class {}, getComputedStyle: style,
    document: {
      readyState,
      title: providerError ? 'Tipsport.cz | Chyba' : 'Tipsport',
      body: {innerText: providerError ? 'Došlo k chybě v požadavku na server.' : ''},
      querySelectorAll(selector) {
        if (selector.startsWith('iframe')) return captcha ? [captcha === 'zero-size' ? {...control('captcha'), getClientRects: () => [{width: 0, height: 0}]} : captcha === 'hidden' ? {...control('captcha'), hiddenByStyle: true} : control('captcha')] : [];
        if (selector.startsWith('input[type="password"]')) return [password];
        if (selector.startsWith('input[name="username"]')) return [username];
        return [];
      }
    }
  };
  context.window.top = context.window;
  const run = submitted => vm.runInNewContext('(' + script('Script') + ')(args)', {...context,
    args: {origin: 'https://www.tipsport.cz', username: 'synthetic-user', password: 'synthetic-password', submitted}});
  return {run, username, password, clicks: () => clicks};
}
const allowed = formCase();
assert.equal(allowed.run(false), 'submitted');
assert.equal(allowed.username.value, 'synthetic-user');
assert.equal(allowed.clicks(), 1);
assert.equal(allowed.run(true), 'submitted');
assert.equal(allowed.clicks(), 1);
for (const [options, expected] of [[{origin: 'https://www.tipsport.cz.evil.test'}, 'wrong-origin'], [{captcha: true}, 'manual-required'], [{providerError: true}, 'provider-error']]) {
  const blocked = formCase(options);
  assert.equal(blocked.run(false), expected);
  assert.equal(blocked.password.value, '');
  assert.equal(blocked.clicks(), 0);
}
for (const captcha of ['zero-size', 'hidden']) {
  const background = formCase({captcha});
  assert.equal(background.run(false), 'submitted');
  assert.equal(background.clicks(), 1);
}
for (const options of [{origin: 'null', href: 'about:blank'}, {readyState: 'loading'}]) {
  const loading = formCase(options);
  assert.equal(loading.run(false), 'waiting');
  assert.equal(loading.password.value, '');
  assert.equal(loading.clicks(), 0);
}
console.log('Provider scripts: guest rejection, signed-in indicators, disabled form, one submission, origin, CAPTCHA and error checks passed.');
