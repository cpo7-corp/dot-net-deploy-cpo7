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
    this.translate.addLangs(['en', 'he']);
    this.translate.setDefaultLang('en');
    this.translate.use('en');

    this.translate.onLangChange.subscribe(event => {
      document.documentElement.dir = event.lang === 'he' ? 'rtl' : 'ltr';
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
