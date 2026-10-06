// Run with Playwright MCP browser_run_code_unsafe after opening the example host's /widgets/ URL.
async (page) => {
    const root = new URL('/widgets/', page.url()).href;
    const check = (condition, message) => { if (!condition) throw new Error(message); };
    const links = async () => page.locator('a').allTextContents();

    await page.goto(root);
    check(JSON.stringify(await links()) === JSON.stringify(['catalog/', 'common/', 'managerGRIST/']),
        'Root must list the example directories without needing index.html');
    await page.getByRole('link', { name: 'managerGRIST/', exact: true }).click();
    check(page.url() === root + 'managerGRIST/', 'Directory link must stay under /widgets/');
    check(JSON.stringify(await links()) === JSON.stringify(['../', 'index.html', 'manager.js', 'style.css']),
        'A directory containing index.html must still show its files');
    await page.getByRole('link', { name: 'index.html', exact: true }).click();
    check(await page.title() === 'Manager widget', 'Explicit index.html must open the widget');
    await page.waitForFunction(() => document.body.dataset.widget === 'managerGRIST', null, { timeout: 5000 });
    check(await page.locator('body').evaluate(el => getComputedStyle(el).marginTop) === '32px',
        'Widget CSS must resolve relative to the explicit HTML file');
    await page.goBack();
    await page.getByRole('link', { name: '../', exact: true }).click();
    check(page.url() === root, 'Parent link must return to the widget root');
    check(!(await links()).includes('../'), 'Root must not link outside the configured tree');
    await page.getByRole('link', { name: 'common/', exact: true }).click();
    await page.getByRole('link', { name: 'notes & виджет.txt', exact: true }).click();
    check((await page.locator('body').innerText()).includes('Widget file with spaces, an ampersand, and Unicode.'),
        'Encoded file link must retrieve the correct bytes');
    await page.goto(root + 'catalog/?cache=1');
    check((await links()).includes('index.html'), 'Query string must preserve directory listing');
    await page.getByRole('link', { name: 'index.html', exact: true }).click();
    check(await page.title() === 'Catalog widget', 'Catalog file must open explicitly');
    await page.goto(root);
    return 'Verified directory browsing, parent navigation, explicit widgets, JS/CSS, Unicode links, and query strings.';
}
