import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TranslateModule } from '@ngx-translate/core';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { ServiceStatus } from '../../models/api-models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, TranslateModule],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.less'
})
export class DashboardComponent implements OnInit {
  private monitor = inject(ServicesMonitorService);

  services = signal<ServiceStatus[]>([]);
  loading = signal<boolean>(true);

  ngOnInit() {
    this.monitor.getAll().subscribe({
      next: (data) => {
        this.services.set(data);
        this.loading.set(false);
      },
      error: (err) => {
        console.error('Failed to load services', err);
        this.loading.set(false);
      }
    });
  }
}
