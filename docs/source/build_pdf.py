import asyncio, sys
from playwright.async_api import async_playwright
# Usage: python3 build_pdf.py document.html ../TechSolutions_Arquitectura_Azure_AtilioCamargo.pdf pdf  (needs: pip install playwright; playwright install chromium)
async def main(src, out, pdf):
    async with async_playwright() as p:
        b = await p.chromium.launch()
        pg = await b.new_page(viewport={'width':900,'height':440}, device_scale_factor=2)
        await pg.goto('file://'+__import__('os').path.abspath(src)); await pg.wait_for_timeout(300)
        if pdf=='pdf':
            await pg.pdf(path=out, format='A4', print_background=True, prefer_css_page_size=True)
        else:
            await pg.screenshot(path=out, full_page=True)
        await b.close()
asyncio.run(main(sys.argv[1], sys.argv[2], sys.argv[3]))
