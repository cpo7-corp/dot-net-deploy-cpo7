import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslateModule],
  templateUrl: './app.html',
  styleUrl: './app.less'
})
export class App {
  title = 'Deploy.NET';
  private translate = inject(TranslateService);

  constructor() {
    this.translate.addLangs(['en', 'zh', 'hi', 'es', 'fr', 'ar', 'bn', 'pt', 'ru', 'ur', 'id', 'de', 'ja', 'sw', 'mr', 'te', 'tr', 'vi', 'ta', 'ko', 'he', 'nl', 'pl', 'sv', 'it', 'no', 'da', 'fi', 'uk', 'ro']);
    const savedLang = localStorage.getItem('app_lang') || 'en';
    this.translate.setDefaultLang('en');
    this.translate.use(savedLang);

    this.translate.onLangChange.subscribe(event => {
      localStorage.setItem('app_lang', event.lang);
      const rtlLangs = ['he', 'ar', 'ur'];
      document.documentElement.dir = rtlLangs.includes(event.lang) ? 'rtl' : 'ltr';
      document.documentElement.lang = event.lang;
    });
  }

  get currentLang() {
    return this.translate.currentLang;
  }

  setLanguage(lang: string) {
    this.translate.use(lang);
  }
}
