// The admin-page translation layer is inline with the dashboard. Exercise its language
// selection and text translation without needing Jellyfin's dashboard runtime.
const fs = require('fs');
const vm = require('vm');

const source = fs.readFileSync('Web/profilesDashboard.html', 'utf8');
const start = source.indexOf('const ADMIN_TR = {');
const end = source.indexOf('// Every value this page renders came from the server', start);
if (start < 0 || end < 0) {
    console.error('could not find the admin translation layer');
    process.exit(1);
}

const translationLayer = source.slice(start, end);
let passed = 0;
const failed = [];

function check(name, condition) {
    if (condition) {
        passed++;
        console.log('  PASS  ' + name);
    } else {
        failed.push(name);
        console.log('  FAIL  ' + name);
    }
}

function build(languages) {
    const document = {
        documentElement: {},
        getElementById() { return null; },
        createTreeWalker(root) {
            let index = 0;
            return { nextNode() { return root.textNodes[index++] || null; } };
        }
    };
    const context = {
        navigator: { languages, language: languages[0] || '' },
        document,
        NodeFilter: { SHOW_TEXT: 4 }
    };
    vm.createContext(context);
    new vm.Script(translationLayer + '\nglobalThis.translate = adminTranslation; globalThis.translateTree = translateAdminTree; globalThis.language = adminLanguage;')
        .runInContext(context);
    return context;
}

const tr = build(['tr-TR', 'en-US']);
check('Turkish browser language is selected by base locale', tr.language === 'tr');
check('static dashboard copy is translated', tr.translate('Bonfire Settings') === 'Bonfire ayarları');
check('dynamic status text with counts is translated',
    tr.translate('Live injection is working — 3 pages served.') === 'Canlı ekleme çalışıyor — 3 sayfa sunuldu.');
check('variable confirmation text is translated',
    tr.translate('Sign Alice out of this device?') === 'Alice bu cihazdan çıkarılsın mı?');
check('runtime version message is translated',
    tr.translate('Running 1.0.0. Version 1.1.0 is installed and starts once the server process restarts.')
        === 'Çalışan sürüm 1.0.0. 1.1.0 sürümü yüklendi; sunucu yeniden başlatıldığında çalışmaya başlayacak.');

const visibleText = { nodeType: 3, nodeValue: 'Cancel', parentElement: { closest() { return null; } } };
const userText = { nodeType: 3, nodeValue: 'Cancel', parentElement: { closest() { return {}; } } };
const root = {
    nodeType: 1,
    textNodes: [visibleText, userText],
    matches() { return false; },
    querySelectorAll() { return []; }
};
tr.translateTree(root);
check('dashboard labels are translated in text nodes', visibleText.nodeValue === 'İptal');
check('user-provided names can be excluded from translation', userText.nodeValue === 'Cancel');

const en = build(['en-US']);
check('English remains unchanged', en.translate('Bonfire Settings') === 'Bonfire Settings');

if (failed.length) {
    failed.forEach(name => console.log('   - ' + name));
    console.log(passed + ' passed, ' + failed.length + ' failed');
    process.exit(1);
}
console.log(passed + ' passed, 0 failed');
