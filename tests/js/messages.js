/*
 * Messages and errors on the client: contractions, one word for the idle re-ask
 * (Auto-lock), no "lock"/"unlock" for wrong PIN guesses, buttons that say what they do,
 * and no hint or card subtitle that repeats its own label.
 *
 * Every changed key is compared exactly, key by key, in English and in each language
 * file, so a failure names the key. The removed keys must be gone from the source, from
 * every language file and from the markup that used them.
 *
 *     node tests/js/messages.js /path/to/old/profiles.js
 */
'use strict';

const fs = require('fs');
const path = require('path');
const L = require('./_lib');

let pass = 0;
const fails = [];
function ok(name, cond, detail) {
    if (cond) { pass++; }
    else { fails.push(name); console.log('  FAIL  ' + name + (detail ? ' - ' + detail : '')); }
}

const src = L.readProfiles();
const english = {};
const start = src.indexOf('const EN_STRINGS = {');
const end = src.indexOf('\n    };', start);
src.slice(start, end).split(/\r?\n/).forEach(line => {
    const m = /^\s*'([^']+)':\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")\s*,?\s*$/.exec(line);
    if (m) english[m[1]] = (m[2] !== undefined ? m[2].replace(/\\'/g, "'") : m[3].replace(/\\"/g, '"'));
});
ok('EN_STRINGS was read', Object.keys(english).length > 400, Object.keys(english).length + ' keys');

const EN = {
    'common.submit': 'Continue',
    'common.unlock': 'Open',
    'pin.lockedMoment': 'Too many wrong PINs. Try again in a moment.',
    'pin.lockedSeconds': 'Too many wrong PINs. Try again in {count} seconds.',
    'pin.lockedMinute': 'Too many wrong PINs. Try again in a minute.',
    'pin.lockedMinutes': 'Too many wrong PINs. Try again in {count} minutes.',
    'errors.failedDeleteDevice': "Couldn't delete the device.",
    'errors.failedRenameDevice': "Couldn't rename the device.",
    'errors.failedMergeDevice': "Couldn't merge the devices.",
    'errors.savingProfile': "Couldn't save the profile.",
    'errors.loadProfileDetails': "Couldn't load the profile.",
    'errors.failedDeleteProfile': "Couldn't delete the profile.",
    'errors.deletingProfile': "Couldn't delete the profile.",
    'errors.couldNotSaveArtwork': "Couldn't save the artwork.",
    'errors.couldNotSaveThat': "Couldn't save that.",
    'errors.heicUnsupported': "HEIC images aren't supported. Export the image as JPEG first.",
    'errors.fileUnreadable': "Couldn't read that file. Choose another.",
    'errors.imageEmpty': 'That image is empty. Choose another.',
    'errors.imageLoadFailed': "Couldn't load that image. Choose another.",
    'panic.dialogBody': "Enter the code your server administrator set. This turns the profile switcher off until Jellyfin restarts. It doesn't open anyone else's profile.",
    'account.emailOff': "Email isn't set up. Ask your server admin to turn it on in the Bonfire dashboard.",
    'switcher.failedBody': "That didn't work. Try again.",
    'switcher.notSavedBody': "This device couldn't save the change. You're still signed in as before.",
    'bonfire.failedToJoin': "Couldn't join the Bonfire.",
    'bonfire.failedNewCode': "Couldn't replace the code.",
    'bonfire.failedKick': "Couldn't remove the member.",
    'bonfire.failedDeleteGroup': "Couldn't delete the Bonfire.",
    'bonfire.failedLeaveGroup': "Couldn't leave the Bonfire.",
    'bonfire.failedGenerateCode': "Couldn't create the code. Try again.",
    'bonfire.failedLoadStatus': "Couldn't load Bonfire. Try again.",
    'bonfire.lanSwitchHintExtra': ", and anything without a PIN can't be opened remotely at all",
    'bonfire.settingsSaveFailed': "Couldn't save. Nothing was changed.",
    'refused.limitsInvalid': 'Check the viewing limits, then try again.',
    'profileForm.autoLock': 'Auto-lock',
    'profileForm.sectionLockingTitle': 'Auto-lock',
    'profileForm.sectionSecuritySubtitle': 'PIN and auto-lock',
    'accountEmail.notifyLockout': 'Email me after too many wrong PINs',
};
Object.keys(EN).forEach(k => ok('en ' + k, english[k] === EN[k], JSON.stringify(english[k])));

// Cut: a hint or subtitle that repeats its own label.
const CUT = ['profileForm.bypassPinHint', 'profileForm.themeHint', 'profileForm.themeSubtitle',
    'profileForm.sectionLockingSubtitle', 'profileForm.sectionRestrictionsSubtitle',
    'account.signInSubtitle'];
const LANGS = ['ca', 'es', 'fr', 'pl', 'tr'];
const files = {};
LANGS.forEach(l => { files[l] = JSON.parse(fs.readFileSync(path.join(L.i18nDir(), l + '.json'), 'utf8')); });
CUT.forEach(k => {
    ok('cut from EN_STRINGS ' + k, !(k in english));
    ok('cut from the markup ' + k, src.indexOf("t('" + k + "')") === -1);
    LANGS.forEach(l => ok('cut from ' + l + ' ' + k, !(k in files[l])));
});

// Nothing a person reads says lock, locked or unlock for wrong guesses.
Object.keys(english).forEach(k => {
    if (/^(pin|panic)\./.test(k)) ok(k + ' has no lock word', !/lock/i.test(english[k]), english[k]);
    ok(k + ' has no "Unlock"', !/\bunlock/i.test(english[k]), english[k]);
});
LANGS.forEach(l => ok(l + ' pin.locked* names no lock', !/bloc|verrouill|blokad|kilit/i.test(
    ['pin.lockedMoment', 'pin.lockedSeconds', 'pin.lockedMinute', 'pin.lockedMinutes'].map(k => files[l][k]).join(' '))));

const TR = {
    ca: {
        'common.unlock': 'Obre', 'common.submit': 'Continua',
        'panic.dialogBody': "Introdueix el codi definit per l'administrador del servidor. Això desactiva el selector de perfils fins que es reiniciï Jellyfin. No obre el perfil de ningú més.",
        'refused.limitsInvalid': 'Revisa els límits de visionat i torna-ho a provar.',
        'errors.heicUnsupported': 'Les imatges HEIC no són compatibles. Exporta primer la imatge en JPEG.',
        'profileForm.autoLock': 'Bloqueig automàtic', 'profileForm.sectionLockingTitle': 'Bloqueig automàtic',
        'profileForm.sectionSecuritySubtitle': 'PIN i bloqueig automàtic',
        'accountEmail.notifyLockout': "Envia'm un correu després de massa PIN incorrectes",
        'errors.fileUnreadable': "No s'ha pogut llegir el fitxer. Tria'n un altre.",
        'errors.imageLoadFailed': "No s'ha pogut carregar la imatge. Tria'n una altra.",
        'errors.imageEmpty': "La imatge és buida. Tria'n una altra.",
        'pin.lockedMoment': "Massa PIN incorrectes. Torna-ho a provar d'aquí a un moment.",
        'pin.lockedMinutes': "Massa PIN incorrectes. Torna-ho a provar d'aquí a {count} minuts.",
    },
    es: {
        'common.unlock': 'Abrir', 'common.submit': 'Continuar',
        'panic.dialogBody': 'Introduce el código que definió el administrador del servidor. Esto apaga el selector de perfiles hasta que se reinicie Jellyfin. No abre el perfil de nadie más.',
        'refused.limitsInvalid': 'Revisa los límites de visionado e inténtalo de nuevo.',
        'errors.heicUnsupported': 'Las imágenes HEIC no son compatibles. Exporta primero la imagen como JPEG.',
        'profileForm.autoLock': 'Bloqueo automático', 'profileForm.sectionLockingTitle': 'Bloqueo automático',
        'profileForm.sectionSecuritySubtitle': 'PIN y bloqueo automático',
        'accountEmail.notifyLockout': 'Avisarme por correo tras demasiados PIN incorrectos',
        'errors.fileUnreadable': 'No se pudo leer ese archivo. Elige otro.',
        'errors.imageLoadFailed': 'No se pudo cargar esa imagen. Elige otra.',
        'errors.imageEmpty': 'Esa imagen está vacía. Elige otra.',
        'pin.lockedMoment': 'Demasiados PIN incorrectos. Inténtalo de nuevo en un momento.',
        'pin.lockedMinutes': 'Demasiados PIN incorrectos. Inténtalo de nuevo en {count} minutos.',
    },
    fr: {
        'common.unlock': 'Ouvrir', 'common.submit': 'Continuer',
        'panic.dialogBody': "Entrez le code défini par votre administrateur serveur. Cela désactive le sélecteur de profils jusqu'au redémarrage de Jellyfin. Cela n'ouvre le profil de personne d'autre.",
        'refused.limitsInvalid': 'Vérifiez les limites de visionnage, puis réessayez.',
        'errors.heicUnsupported': "Les images HEIC ne sont pas prises en charge. Exportez d'abord l'image en JPEG.",
        'profileForm.autoLock': 'Verrouillage automatique', 'profileForm.sectionLockingTitle': 'Verrouillage automatique',
        'profileForm.sectionSecuritySubtitle': 'Code PIN et verrouillage automatique',
        'accountEmail.notifyLockout': "M'avertir par e-mail après trop de codes PIN incorrects",
        'errors.fileUnreadable': 'Impossible de lire ce fichier. Choisissez-en un autre.',
        'errors.imageLoadFailed': 'Impossible de charger cette image. Choisissez-en une autre.',
        'errors.imageEmpty': 'Cette image est vide. Choisissez-en une autre.',
    },
    pl: {
        'common.unlock': 'Otwórz', 'common.submit': 'Dalej',
        'panic.dialogBody': 'Wprowadź kod ustawiony przez administratora serwera. Wyłączy to ekran wyboru profili do czasu ponownego uruchomienia Jellyfin. Nie otwiera to profilu innych użytkowników.',
        'refused.limitsInvalid': 'Sprawdź limity oglądania, a potem spróbuj ponownie.',
        'errors.heicUnsupported': 'Obrazy HEIC nie są obsługiwane. Wyeksportuj obraz w formacie JPEG.',
        'profileForm.autoLock': 'Automatyczne blokowanie', 'profileForm.sectionLockingTitle': 'Automatyczne blokowanie',
        'profileForm.sectionSecuritySubtitle': 'Kod PIN i automatyczne blokowanie',
        'accountEmail.notifyLockout': 'Wyślij e-mail po zbyt wielu błędnych kodach PIN',
        'errors.fileUnreadable': 'Nie można odczytać tego pliku. Wybierz inny.',
        'errors.imageLoadFailed': 'Nie można wczytać tego obrazu. Wybierz inny.',
        'errors.imageEmpty': 'Ten obraz jest pusty. Wybierz inny.',
        'pin.lockedMoment': 'Zbyt wiele błędnych kodów PIN. Spróbuj ponownie za chwilę.',
        'pin.lockedMinutes': 'Zbyt wiele błędnych kodów PIN. Spróbuj ponownie za {count} min.',
    },
    tr: {
        'common.unlock': 'Aç', 'common.submit': 'Devam',
        'panic.dialogBody': 'Sunucu yöneticinizin belirlediği kodu girin. Bu işlem, Jellyfin yeniden başlatılana kadar profil seçicisini kapatır. Başka birinin profilini açmaz.',
        'refused.limitsInvalid': 'İzleme sınırlarını kontrol edip tekrar deneyin.',
        'errors.heicUnsupported': 'HEIC görselleri desteklenmiyor. Önce görseli JPEG olarak dışa aktarın.',
        'profileForm.autoLock': 'Otomatik kilitleme', 'profileForm.sectionLockingTitle': 'Otomatik kilitleme',
        'profileForm.sectionSecuritySubtitle': 'PIN ve otomatik kilitleme',
        'accountEmail.notifyLockout': 'Çok fazla hatalı PIN girildiğinde bana e-posta gönder',
        'errors.fileUnreadable': 'Dosya okunamadı. Başka bir dosya seçin.',
        'errors.imageLoadFailed': 'Görsel yüklenemedi. Başka bir görsel seçin.',
        'errors.imageEmpty': 'Görsel boş. Başka bir görsel seçin.',
    },
};
LANGS.forEach(l => Object.keys(TR[l]).forEach(k => ok(l + ' ' + k, files[l][k] === TR[l][k], JSON.stringify(files[l][k]))));
// panic.dialogBody: the old sentence said "unlock" in each language.
const unlockWord = { ca: /desbloqu/i, es: /desbloque/i, fr: /déverrouill/i, pl: /odblok/i, tr: /kilidini/i };
LANGS.forEach(l => ok(l + ' panic.dialogBody does not say unlock', !unlockWord[l].test(files[l]['panic.dialogBody']), files[l]['panic.dialogBody']));

// R-1178: the profile is saved before its viewing hours are, so a failure of the hours
// must not say the profile was not saved (and must not show the server's text alone).
{
    const calls = [];
    for (let i = src.indexOf('viewingEditor.save('); i !== -1; i = src.indexOf('viewingEditor.save(', i + 1)) calls.push(i);
    ok('the profile form saves its viewing hours in two places', calls.length === 2, String(calls.length));
    calls.forEach((i, n) => {
        const after = src.slice(i, i + 400);
        ok('hours save ' + (n + 1) + ' words its failure as the hours', after.indexOf('_hoursFailText') !== -1
            && after.indexOf('errors.savingProfile') === -1, after.slice(0, 160).replace(/\s+/g, ' '));
    });
    ok('the hours message is in English', /saved, but not its viewing hours/.test(english['errors.savingHours'] || ''));
    ['ca', 'es', 'fr', 'pl', 'tr'].forEach(l => ok(l + ' has errors.savingHours',
        typeof files[l]['errors.savingHours'] === 'string' && files[l]['errors.savingHours'].length > 10));
}

if (fails.length) { console.log('\n' + pass + ' passed, ' + fails.length + ' failed'); process.exit(1); }
console.log(pass + ' passed, 0 failed');
