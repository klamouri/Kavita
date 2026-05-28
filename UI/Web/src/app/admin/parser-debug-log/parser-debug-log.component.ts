import {ChangeDetectionStrategy, ChangeDetectorRef, Component, DestroyRef, inject, OnInit} from '@angular/core';
import {CommonModule} from '@angular/common';
import {FormControl, FormGroup, ReactiveFormsModule} from '@angular/forms';
import {TranslocoDirective} from '@jsverse/transloco';
import {takeUntilDestroyed} from '@angular/core/rxjs-interop';
import {finalize} from 'rxjs';
import {ParserLogService, ParserDebugEntry, ParserOutcome} from '../../_services/parser-log.service';
import {UtcToLocalTimePipe} from '../../_pipes/utc-to-local-time.pipe';

type OutcomeFilter = {
  accepted: boolean;
  lenient: boolean;
  rejected: boolean;
};

@Component({
  selector: 'app-parser-debug-log',
  templateUrl: './parser-debug-log.component.html',
  styleUrls: ['./parser-debug-log.component.scss'],
  imports: [CommonModule, ReactiveFormsModule, TranslocoDirective, UtcToLocalTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ParserDebugLogComponent implements OnInit {
  private readonly parserLogService = inject(ParserLogService);
  private readonly cdRef = inject(ChangeDetectorRef);
  private readonly destroyRef = inject(DestroyRef);

  filterForm = new FormGroup({
    libraryId: new FormControl<number>(0, {nonNullable: true}),
    limit: new FormControl<number>(500, {nonNullable: true}),
    showAccepted: new FormControl<boolean>(true, {nonNullable: true}),
    showLenient: new FormControl<boolean>(true, {nonNullable: true}),
    showRejected: new FormControl<boolean>(true, {nonNullable: true}),
  });

  rawEntries: ParserDebugEntry[] = [];
  filteredEntries: ParserDebugEntry[] = [];
  isLoading = false;

  readonly ParserOutcome = ParserOutcome;

  ngOnInit() {
    this.refresh();
    this.filterForm.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.applyClientFilter());
  }

  refresh() {
    this.isLoading = true;
    this.cdRef.markForCheck();
    const {libraryId, limit} = this.filterForm.getRawValue();
    this.parserLogService.getParserDebugLog(libraryId, limit)
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => {
        this.isLoading = false;
        this.cdRef.markForCheck();
      }))
      .subscribe(entries => {
        this.rawEntries = entries;
        this.applyClientFilter();
      });
  }

  clear() {
    this.parserLogService.clearParserDebugLog()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh());
  }

  private applyClientFilter() {
    const {showAccepted, showLenient, showRejected} = this.filterForm.getRawValue();
    this.filteredEntries = this.rawEntries.filter(e => {
      if (e.outcome === ParserOutcome.Accepted) return showAccepted;
      if (e.outcome === ParserOutcome.Lenient) return showLenient;
      return showRejected;
    });
    this.cdRef.markForCheck();
  }

  trackBy = (idx: number, item: ParserDebugEntry) => `${item.timestampUtc}-${item.filePath}`;
}
