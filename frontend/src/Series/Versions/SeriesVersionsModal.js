import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import InteractiveSearch from 'InteractiveSearch/InteractiveSearch';
import QualityProfileNameConnector from 'Settings/Profiles/Quality/QualityProfileNameConnector';
import { cancelFetchReleases, clearReleases } from 'Store/Actions/releaseActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';

const columns = [
  { name: 'episode', label: () => translate('Episode'), isVisible: true },
  { name: 'title', label: () => translate('Title'), isVisible: true },
  { name: 'file', label: () => translate('SeriesVersionsFile'), isVisible: true },
  { name: 'actions', label: () => translate('Search'), isVisible: true }
];

class SeriesVersionsModal extends Component {
  constructor(props, context) {
    super(props, context);
    this.state = { versions: [], versionId: '', season: '', loading: true, error: null, search: null };
    this._mounted = false;
    this._abort = null;
    this._generation = 0;
  }

  componentDidMount() {
    this._mounted = true;
    this.onRefresh();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.seriesId !== this.props.seriesId) {
      this._abort?.();
      this.props.onClearSearch();
      this.setState({ versions: [], versionId: '', season: '', search: null });
      this.onRefresh();
    }
  }

  componentWillUnmount() {
    this._mounted = false;
    this._abort?.();
    this.props.onClearSearch();
  }

  onRefresh = () => {
    const generation = ++this._generation;
    this.setState({ loading: true, error: null });
    const { request, abortRequest } = createAjaxRequest({ url: `/series/${this.props.seriesId}/versions`, dataType: 'json' });
    this._abort = abortRequest;
    request.done((versions) => {
      if (this._mounted && generation === this._generation) {
        this.setState(({ versionId, season }) => {
          const selected = versions.find((v) => String(v.id) === versionId) || versions[0];
          const episodes = selected?.episodes || [];
          return {
            versions,
            versionId: selected ? String(selected.id) : '',
            season: episodes.some((e) => String(e.seasonNumber) === season) ? season : String(episodes[0]?.seasonNumber ?? '')
          };
        });
      }
    }).fail((xhr) => {
      if (this._mounted && generation === this._generation && !xhr.aborted) {
        this.setState({ error: xhr.responseJSON?.message || translate('SeriesVersionsLoadError'), versions: [] });
      }
    }).always(() => {
      if (this._mounted && generation === this._generation) {
        this.setState({ loading: false });
      }
    });
  };

  onVersionChange = ({ value }) => this.setState({ versionId: value });

  onSeasonChange = ({ value }) => this.setState({ season: value });

  onSearch = (episodeId) => {
    this.props.onClearSearch();
    this.setState({ search: { episodeId, seriesVersionId: Number(this.state.versionId) } });
  };

  onBack = () => {
    this.props.onClearSearch();
    this.setState({ search: null });
    this.onRefresh();
  };

  render() {
    const { onModalClose } = this.props;
    const { versions, versionId, season, loading, error, search } = this.state;
    const version = versions.find((v) => String(v.id) === versionId);
    const episodes = version?.episodes || [];
    const seasons = [...new Set(episodes.map((e) => e.seasonNumber))];

    return (
      <Modal
        isOpen={true}
        size={sizes.EXTRA_EXTRA_LARGE}
        closeOnBackgroundClick={false}
        onModalClose={onModalClose}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>{translate('SeriesVersionsTitle')}</ModalHeader>
          <ModalBody>
            {
              loading &&
                <LoadingIndicator />
            }
            {
              error &&
                <Alert kind={kinds.DANGER}>{error}</Alert>
            }
            {
              !loading && !error && !versions.length &&
                <Alert>{translate('SeriesVersionsOrdinary')}</Alert>
            }
            {
              !loading && version &&
                <div>
                  <FormGroup>
                    <FormLabel>{translate('SeriesVersionsVersion')}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="version"
                      value={versionId}
                      values={versions.map((v) => ({ key: String(v.id), value: v.isPrimary ? `${v.label} (${translate('SeriesConversionPrimary')})` : v.label }))}
                      isDisabled={!!search}
                      onChange={this.onVersionChange}
                    />
                  </FormGroup>
                  <p><QualityProfileNameConnector qualityProfileId={version.qualityProfileId} /></p>
                  <p>{translate('SeriesVersionsSummary', { assigned: episodes.filter((e) => e.episodeFileId).length, total: episodes.length, reserved: episodes.filter((e) => e.isReserved).length })}</p>
                  <Alert kind={kinds.INFO}>{translate('SeriesVersionsSearchHelp')}</Alert>
                  {
                    search ?
                      <InteractiveSearch type="episode" searchPayload={search} /> :
                      <div>
                        {
                          episodes.length === 0 &&
                            <Alert>{translate('NoEpisodeInformation')}</Alert>
                        }
                        {
                          episodes.length > 0 &&
                            <FormGroup>
                              <FormLabel>{translate('Season')}</FormLabel>
                              <FormInputGroup
                                type={inputTypes.SELECT}
                                name="season"
                                value={season}
                                values={seasons.map((s) => ({ key: String(s), value: String(s) }))}
                                onChange={this.onSeasonChange}
                              />
                            </FormGroup>
                        }
                        <Table columns={columns}>
                          <TableBody>
                            {
                              episodes.filter((e) => String(e.seasonNumber) === season).map((episode) => (
                                <TableRow key={episode.episodeId}>
                                  <TableRowCell>{episode.seasonNumber}x{String(episode.episodeNumber).padStart(2, '0')}</TableRowCell>
                                  <TableRowCell>{episode.title}</TableRowCell>
                                  <TableRowCell>
                                    {episode.episodeFileId ? `${episode.relativePath} (${formatBytes(episode.size)})` : translate(episode.isReserved ? 'SeriesVersionsReserved' : 'SeriesVersionsEmpty')}
                                  </TableRowCell>
                                  <TableRowCell>
                                    <Button
                                      isDisabled={!!episode.episodeFileId || episode.isReserved}
                                      onPress={() => this.onSearch(episode.episodeId)}
                                    >
                                      {translate('Search')}
                                    </Button>
                                  </TableRowCell>
                                </TableRow>
                              ))
                            }
                          </TableBody>
                        </Table>
                      </div>
                  }
                </div>
            }
          </ModalBody>
          <ModalFooter>
            {
              search ?
                <Button onPress={this.onBack}>{translate('SeriesVersionsBack')}</Button> :
                <Button isDisabled={loading} onPress={this.onRefresh}>{translate('Refresh')}</Button>
            }
            <Button onPress={onModalClose}>{translate('Close')}</Button>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

SeriesVersionsModal.propTypes = {
  seriesId: PropTypes.number.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onClearSearch: PropTypes.func.isRequired
};

export default connect(null, (dispatch) => ({
  onClearSearch: () => {
    dispatch(cancelFetchReleases());
    dispatch(clearReleases());
  }
}))(SeriesVersionsModal);
